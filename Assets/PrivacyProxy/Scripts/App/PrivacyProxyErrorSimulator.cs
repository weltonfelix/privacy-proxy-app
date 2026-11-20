using UnityEngine;

public sealed class PrivacyProxyErrorSimulator : MonoBehaviour
{
    [SerializeField] bool simulateCameraPermissionDenied;
    [SerializeField] bool simulateEmptyMask;
    [SerializeField] bool simulateQwenFailure;
    [SerializeField] bool simulateMissingModelFiles;

    public bool CameraPermissionDenied => simulateCameraPermissionDenied;
    public bool EmptyMask => simulateEmptyMask;
    public bool MissingModelFiles => simulateMissingModelFiles;

    public bool ConsumeQwenFailure()
    {
        if (!simulateQwenFailure) return false;
        simulateQwenFailure = false;
        return true;
    }
}
