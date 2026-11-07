using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace CassiniFlow.Infrastructure.Adapters;

/// <summary>
/// P/Invoke natif officiel pour le SDK ZWO ASI (ASICamera2.dll sous Windows x64, libASICamera2.so sous Linux).
/// </summary>
internal static unsafe class ZwoAsiNative
{
    public const string LibraryName = "ASICamera2";
    private static bool _resolverRegistered;
    private static readonly object ResolverLock = new();

    static ZwoAsiNative()
    {
        EnsureResolverRegistered();
    }

    public static void EnsureResolverRegistered()
    {
        lock (ResolverLock)
        {
            if (_resolverRegistered) return;
            _resolverRegistered = true;
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(ZwoAsiNative).Assembly, ResolveAsiLibrary);
            }
            catch
            {
                // Ignorer si un resolver est déjà enregistré sur l'assembly
            }
        }
    }

    private static IntPtr ResolveAsiLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        string baseDir = AppContext.BaseDirectory;
        string[] candidates = OperatingSystem.IsWindows()
            ? [
                Path.Combine(baseDir, "ASICamera2.dll"),
                Path.Combine(baseDir, "x64", "ASICamera2.dll"),
                @"C:\Program Files\ZWO Design\ASIStudio\ASICamera2.dll",
                @"C:\Program Files\ASICap\ASICamera2.dll",
                @"C:\Program Files (x86)\ZWO Design\ASIStudio\ASICamera2.dll",
                "ASICamera2.dll"
              ]
            : [
                Path.Combine(baseDir, "libASICamera2.so"),
                "/usr/local/lib/libASICamera2.so",
                "/usr/lib/libASICamera2.so",
                "libASICamera2.so"
              ];

        foreach (string candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out IntPtr handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    public enum ASI_BAYER_PATTERN : int
    {
        ASI_BAYER_RG = 0,
        ASI_BAYER_BG = 1,
        ASI_BAYER_GR = 2,
        ASI_BAYER_GB = 3
    }

    public enum ASI_IMG_TYPE : int
    {
        ASI_IMG_RAW8 = 0,
        ASI_IMG_RGB24 = 1,
        ASI_IMG_RAW16 = 2,
        ASI_IMG_Y8 = 3,
        ASI_IMG_END = -1
    }

    public enum ASI_CONTROL_TYPE : int
    {
        ASI_GAIN = 0,
        ASI_EXPOSURE = 1, // Microsecondes
        ASI_GAMMA = 2,
        ASI_WB_R = 3,
        ASI_WB_B = 4,
        ASI_OFFSET = 5,
        ASI_BANDWIDTHOVERLOAD = 6,
        ASI_OVERCLOCK = 7,
        ASI_TEMPERATURE = 8, // 10 * °C
        ASI_FLIP = 9,
        ASI_AUTO_MAX_GAIN = 10,
        ASI_AUTO_MAX_EXP = 11,
        ASI_AUTO_TARGET_BRIGHTNESS = 12,
        ASI_HARDWARE_BIN = 13,
        ASI_HIGH_SPEED_MODE = 14
    }

    public enum ASI_ERROR_CODE : int
    {
        ASI_SUCCESS = 0,
        ASI_ERROR_INVALID_INDEX = 1,
        ASI_ERROR_INVALID_ID = 2,
        ASI_ERROR_INVALID_CONTROL_TYPE = 3,
        ASI_ERROR_CAMERA_CLOSED = 4,
        ASI_ERROR_CAMERA_REMOVED = 5,
        ASI_ERROR_INVALID_PATH = 6,
        ASI_ERROR_INVALID_FILEFORMAT = 7,
        ASI_ERROR_INVALID_SIZE = 8,
        ASI_ERROR_INVALID_IMGTYPE = 9,
        ASI_ERROR_OUTOF_BOUNDARY = 10,
        ASI_ERROR_TIMEOUT = 11,
        ASI_ERROR_INVALID_SEQUENCE = 12,
        ASI_ERROR_BUFFER_TOO_SMALL = 13,
        ASI_ERROR_VIDEO_MODE_ACTIVE = 14,
        ASI_ERROR_EXPOSURE_IN_PROGRESS = 15,
        ASI_ERROR_GENERAL_ERROR = 16,
        ASI_ERROR_END = 17
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct ASI_CAMERA_INFO
    {
        public fixed byte NameBytes[64];
        public int CameraID;
        public CLong MaxHeight;
        public CLong MaxWidth;
        public int IsColorCam;
        public ASI_BAYER_PATTERN BayerPattern;
        public fixed int SupportedBins[16];
        public fixed int SupportedVideoFormat[8];
        public double PixelSize;
        public int MechanicalShutter;
        public int ST4Port;
        public int IsCoolerCam;
        public int IsUSB3Host;
        public int IsUSB3Camera;
        public float ElecPerADU;
        public int BitDepth;
        public int IsTriggerCam;
        public fixed byte Unused[16];

        public string GetName()
        {
            fixed (byte* p = NameBytes)
            {
                int len = 0;
                while (len < 64 && p[len] != 0) len++;
                return Encoding.ASCII.GetString(p, len).Trim();
            }
        }
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ASIGetNumOfConnectedCameras();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ASI_ERROR_CODE ASIGetCameraProperty(out ASI_CAMERA_INFO pASICameraInfo, int iCameraIndex);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ASI_ERROR_CODE ASIOpenCamera(int iCameraID);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ASI_ERROR_CODE ASIInitCamera(int iCameraID);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ASI_ERROR_CODE ASICloseCamera(int iCameraID);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ASI_ERROR_CODE ASISetROIFormat(int iCameraID, int iWidth, int iHeight, int iBin, ASI_IMG_TYPE Img_type);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ASI_ERROR_CODE ASIGetROIFormat(int iCameraID, out int piWidth, out int piHeight, out int piBin, out ASI_IMG_TYPE pImg_type);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ASI_ERROR_CODE ASISetStartPos(int iCameraID, int iStartX, int iStartY);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ASI_ERROR_CODE ASISetControlValue(int iCameraID, ASI_CONTROL_TYPE ControlType, CLong lValue, int bAuto);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ASI_ERROR_CODE ASIGetControlValue(int iCameraID, ASI_CONTROL_TYPE ControlType, out CLong plValue, out int pbAuto);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ASI_ERROR_CODE ASIBeginVideoCapture(int iCameraID);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ASI_ERROR_CODE ASIEndVideoCapture(int iCameraID);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ASI_ERROR_CODE ASIGetVideoData(int iCameraID, byte* pBuffer, CLong lBuffSize, int iWaitms);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ASI_ERROR_CODE ASIGetDroppedFrames(int iCameraID, out int piDropFrames);
}
