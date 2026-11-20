using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unity.InferenceEngine;
using UnityEngine;
using Debug = UnityEngine.Debug;

// EdgeSAM on the CPU. SetPhotoAsync runs the encoder once per photo and keeps its output;
// SegmentAsync runs only the decoder for each new box. Call both from the main thread. The model runs
// in Burst jobs, image work on the thread pool, and results are read back without blocking the frame.
// Calls run one after the other, so a box asked for while a photo is still being read waits for it.
public sealed class EdgeSamSegmenter : IDisposable
{
    public sealed class Result
    {
        public string Error; // null when it worked; a message for the user otherwise
        public bool Ok => Error == null;
        public bool[] Mask; // Width*Height, rows bottom-up like Texture2D.GetPixels32; true = object
        public int Width, Height, MaskPixels;
        public float Score;
        public double Seconds; // encoder time for SetPhotoAsync, decoder time for SegmentAsync
    }

    public const string EmptyMaskError = "O retângulo não pegou nenhum objeto. Desenhe o retângulo de novo em volta do objeto.";
    const int MaskSide = 256; // the decoder's low-res mask covers the padded 1024 square

    // 0 schedules the whole encoder at once. More than 0 spreads it over frames, this many layers per frame.
    public int LayersPerFrame;
    public int EncoderRuns { get; private set; } // tests and logs use it to show a new box skips the encoder

    readonly Worker _encoder, _decoder;
    readonly float[] _input = new float[3 * EdgeSamInput.Side * EdgeSamInput.Side]; // reused, 12 MB
    Tensor<float> _embeddings; // encoder output for the current photo
    int _width, _height;
    Task _last = Task.CompletedTask;
    bool _disposed;

    public EdgeSamSegmenter(ModelAsset encoder, ModelAsset decoder)
    {
        _encoder = new Worker(ModelLoader.Load(encoder), BackendType.CPU);
        _decoder = new Worker(ModelLoader.Load(decoder), BackendType.CPU);
    }

    // photo: width*height pixels, rows bottom-up like Texture2D.GetPixels32. The array is read on another
    // thread, so do not change it until the task ends.
    public Task<Result> SetPhotoAsync(Color32[] photo, int width, int height)
    {
        if (photo == null || width <= 0 || height <= 0 || photo.Length != width * height)
            return Task.FromResult(Fail($"A foto veio vazia ou com tamanho errado ({photo?.Length ?? 0} pixels para {width} × {height})."));
        return Enqueue(() => Encode(photo, width, height));
    }

    // box: photo pixels, origin at the top-left corner like the PoC (xMin, yMin, xMax, yMax).
    public Task<Result> SegmentAsync(Rect box) => Enqueue(() => Decode(box));

    // Frees the models only after the running call has finished, never during it.
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        FreeAfter(_last);
    }

    async void FreeAfter(Task last)
    {
        await last; // Run never throws
        _embeddings?.Dispose();
        _embeddings = null;
        _encoder.Dispose();
        _decoder.Dispose();
        Debug.Log("EdgeSAM: modelos liberados");
    }

    async Task<Result> Encode(Color32[] photo, int width, int height)
    {
        _embeddings?.Dispose();
        _embeddings = null; // a failed read must not leave the old photo's output behind
        var timer = Stopwatch.StartNew();
        await Task.Run(() => EdgeSamInput.Prepare(photo, width, height, _input));
        var prepMs = timer.ElapsedMilliseconds;
        if (_disposed)
            return Fail("O EdgeSAM foi fechado."); // skip the ~1 s encoder run nobody will read

        using var input = new Tensor<float>(new TensorShape(1, 3, EdgeSamInput.Side, EdgeSamInput.Side), _input);
        var scheduleTimer = Stopwatch.StartNew();
        long scheduleMs = 0;
        if (LayersPerFrame <= 0)
        {
            _encoder.Schedule(input);
            scheduleMs = scheduleTimer.ElapsedMilliseconds;
        }
        else
        {
            var steps = _encoder.ScheduleIterable(input);
            int layer = 0;
            while (steps.MoveNext())
                if (++layer % LayersPerFrame == 0)
                {
                    scheduleMs += scheduleTimer.ElapsedMilliseconds;
                    await Task.Yield(); // next frame
                    scheduleTimer.Restart();
                }
            scheduleMs += scheduleTimer.ElapsedMilliseconds;
        }
        var embeddings = await (_encoder.PeekOutput() as Tensor<float>).ReadbackAndCloneAsync();
        if (_disposed)
        {
            embeddings.Dispose();
            return Fail("O EdgeSAM foi fechado.");
        }
        _embeddings = embeddings;
        _width = width;
        _height = height;
        EncoderRuns++;
        var seconds = timer.Elapsed.TotalSeconds;
        Debug.Log($"EdgeSAM encoder #{EncoderRuns}: {seconds * 1000:F0} ms (preparo da foto {prepMs} ms, "
                  + $"agendar no quadro {scheduleMs} ms, {(LayersPerFrame > 0 ? $"{LayersPerFrame} camadas por quadro" : "tudo de uma vez")})");
        return new Result { Width = width, Height = height, Seconds = seconds };
    }

    async Task<Result> Decode(Rect box)
    {
        if (_embeddings == null)
            return Fail("Nenhuma foto foi lida ainda.");
        bool finite = float.IsFinite(box.xMin) && float.IsFinite(box.yMin) && float.IsFinite(box.xMax) && float.IsFinite(box.yMax);
        if (!finite || box.width <= 0 || box.height <= 0 || box.xMin < 0 || box.yMin < 0 || box.xMax > _width || box.yMax > _height)
            return Fail($"O retângulo está fora da foto ({box.xMin:F0}, {box.yMin:F0}, {box.xMax:F0}, {box.yMax:F0}).");

        var timer = Stopwatch.StartNew();
        var scaled = EdgeSamInput.ScaledSize(_width, _height);
        float sx = (float)scaled.x / _width, sy = (float)scaled.y / _height;
        // The box becomes two corner points with labels 2 and 3, in 1024-scale coordinates.
        using var points = new Tensor<float>(new TensorShape(1, 2, 2), new[] { box.xMin * sx, box.yMin * sy, box.xMax * sx, box.yMax * sy });
        using var labels = new Tensor<float>(new TensorShape(1, 2), new[] { 2f, 3f });
        _decoder.Schedule(_embeddings, points, labels);
        using var scores = await (_decoder.PeekOutput("scores") as Tensor<float>).ReadbackAndCloneAsync();
        using var masks = await (_decoder.PeekOutput("masks") as Tensor<float>).ReadbackAndCloneAsync();
        if (_disposed)
            return Fail("O EdgeSAM foi fechado.");
        var modelMs = timer.ElapsedMilliseconds;
        var shape = masks.shape;
        if (shape.rank != 4 || shape[2] != MaskSide || shape[3] != MaskSide)
            return Fail($"A máscara veio com tamanho errado ({shape}).");
        var low = masks.DownloadToArray(); // mask 0 (the first 256×256 values) matches num_multimask_outputs=1
        float score = scores.DownloadToArray()[0];
        int w = _width, h = _height;
        var result = await Task.Run(() => MaskFromLowRes(low, w, h));
        result.Score = score;
        result.Seconds = timer.Elapsed.TotalSeconds;
        Debug.Log($"EdgeSAM decoder: {result.Seconds * 1000:F0} ms (modelo {modelMs} ms, ampliar a máscara para a foto {result.Seconds * 1000 - modelMs:F0} ms), score {score:F4}, máscara {result.MaskPixels} px. "
                  + $"Só o decoder rodou, usando a foto já lida pelo encoder #{EncoderRuns}.");
        return result;
    }

    // Bilinear upscale of mask 0 to the photo size, then threshold at 0. Empty or wrong-size masks are errors.
    public static Result MaskFromLowRes(float[] low, int width, int height)
    {
        if (low == null || low.Length < MaskSide * MaskSide || width <= 0 || height <= 0)
            return Fail($"A máscara veio com tamanho errado ({low?.Length ?? 0} valores).");
        // One factor for both axes: per-axis nw/nh crop here lowered IoU vs the PoC (0.9843 vs 0.9857, Editor CPU).
        float s = EdgeSamInput.Scale(width, height) / (EdgeSamInput.Side / MaskSide);
        var mask = new bool[width * height];
        int count = 0;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (Bilinear(low, (x + 0.5f) * s - 0.5f, (y + 0.5f) * s - 0.5f) <= 0)
                continue;
            mask[(height - 1 - y) * width + x] = true;
            count++;
        }
        if (count == 0)
            return Fail(EmptyMaskError);
        return new Result { Mask = mask, Width = width, Height = height, MaskPixels = count };
    }

    static float Bilinear(float[] t, float fx, float fy)
    {
        const int max = MaskSide - 1;
        int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, max), y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, max);
        int x1 = Mathf.Min(x0 + 1, max), y1 = Mathf.Min(y0 + 1, max);
        float ax = Mathf.Clamp01(fx - x0), ay = Mathf.Clamp01(fy - y0);
        float top = Mathf.Lerp(t[y0 * MaskSide + x0], t[y0 * MaskSide + x1], ax);
        float bottom = Mathf.Lerp(t[y1 * MaskSide + x0], t[y1 * MaskSide + x1], ax);
        return Mathf.Lerp(top, bottom, ay);
    }

    static Result Fail(string error) => new() { Error = error };

    Task<Result> Enqueue(Func<Task<Result>> op)
    {
        var task = Run(_last, op);
        _last = task;
        return task;
    }

    async Task<Result> Run(Task previous, Func<Task<Result>> op)
    {
        await previous;
        if (_disposed)
            return Fail("O EdgeSAM foi fechado.");
        try
        {
            return await op();
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            return Fail($"Erro no EdgeSAM: {e.Message}");
        }
    }
}
