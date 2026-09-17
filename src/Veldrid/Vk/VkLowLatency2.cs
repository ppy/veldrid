using Vulkan;

namespace Veldrid.Vk
{
    internal enum VkLatencyMarkerNV
    {
        SimulationStart = 0,
        SimulationEnd = 1,
        RenderSubmitStart = 2,
        RenderSubmitEnd = 3,
        PresentStart = 4,
        PresentEnd = 5,
        InputSample = 6,
        TriggerFlash = 7,
        OutOfBandRenderSubmitStart = 8,
        OutOfBandRenderSubmitEnd = 9,
        OutOfBandPresentStart = 10,
        OutOfBandPresentEnd = 11
    }

    internal enum VkOutOfBandQueueTypeNV
    {
        Render = 0,
        Present = 1
    }

    internal enum VkSemaphoreTypeKHR
    {
        Binary = 0,
        Timeline = 1
    }

    // VK_NV_low_latency2

    internal unsafe struct VkLatencySleepModeInfoNV
    {
        public const VkStructureType TYPE = (VkStructureType)1000505000;

        public VkStructureType SType;
        public void* PNext;
        public uint LowLatencyMode;
        public uint LowLatencyBoost;
        public uint MinimumIntervalUs;

        public static VkLatencySleepModeInfoNV New() => new VkLatencySleepModeInfoNV { SType = TYPE };
    }

    internal unsafe struct VkLatencySleepInfoNV
    {
        public const VkStructureType TYPE = (VkStructureType)1000505001;

        public VkStructureType SType;
        public void* PNext;
        public VkSemaphore SignalSemaphore;
        public ulong Value;

        public static VkLatencySleepInfoNV New() => new VkLatencySleepInfoNV { SType = TYPE };
    }

    internal unsafe struct VkSetLatencyMarkerInfoNV
    {
        public const VkStructureType TYPE = (VkStructureType)1000505002;

        public VkStructureType SType;
        public void* PNext;
        public ulong PresentID;
        public VkLatencyMarkerNV Marker;

        public static VkSetLatencyMarkerInfoNV New() => new VkSetLatencyMarkerInfoNV { SType = TYPE };
    }

    internal unsafe struct VkGetLatencyMarkerInfoNV
    {
        public const VkStructureType TYPE = (VkStructureType)1000505003;

        public VkStructureType SType;
        public void* PNext;
        public uint TimingCount;
        public VkLatencyTimingsFrameReportNV* PTimings;

        public static VkGetLatencyMarkerInfoNV New() => new VkGetLatencyMarkerInfoNV { SType = TYPE };
    }

    internal unsafe struct VkLatencyTimingsFrameReportNV
    {
        public const VkStructureType TYPE = (VkStructureType)1000505004;

        public VkStructureType SType;
        public void* PNext;
        public ulong PresentID;
        public ulong InputSampleTimeUs;
        public ulong SimStartTimeUs;
        public ulong SimEndTimeUs;
        public ulong RenderSubmitStartTimeUs;
        public ulong RenderSubmitEndTimeUs;
        public ulong PresentStartTimeUs;
        public ulong PresentEndTimeUs;
        public ulong DriverStartTimeUs;
        public ulong DriverEndTimeUs;
        public ulong OsRenderQueueStartTimeUs;
        public ulong OsRenderQueueEndTimeUs;
        public ulong GpuRenderStartTimeUs;
        public ulong GpuRenderEndTimeUs;

        public static VkLatencyTimingsFrameReportNV New() => new VkLatencyTimingsFrameReportNV { SType = TYPE };
    }

    internal unsafe struct VkLatencySubmissionPresentIdNV
    {
        public const VkStructureType TYPE = (VkStructureType)1000505005;

        public VkStructureType SType;
        public void* PNext;
        public ulong PresentID;

        public static VkLatencySubmissionPresentIdNV New() => new VkLatencySubmissionPresentIdNV { SType = TYPE };
    }

    internal unsafe struct VkOutOfBandQueueTypeInfoNV
    {
        public const VkStructureType TYPE = (VkStructureType)1000505006;

        public VkStructureType SType;
        public void* PNext;
        public VkOutOfBandQueueTypeNV QueueType;

        public static VkOutOfBandQueueTypeInfoNV New() => new VkOutOfBandQueueTypeInfoNV { SType = TYPE };
    }

    internal unsafe struct VkSwapchainLatencyCreateInfoNV
    {
        public const VkStructureType TYPE = (VkStructureType)1000505007;

        public VkStructureType SType;
        public void* PNext;
        public uint LatencyModeEnable;

        public static VkSwapchainLatencyCreateInfoNV New() => new VkSwapchainLatencyCreateInfoNV { SType = TYPE };
    }

    // VK_KHR_present_id

    internal unsafe struct VkPresentIdKHR
    {
        public const VkStructureType TYPE = (VkStructureType)1000294000;

        public VkStructureType SType;
        public void* PNext;
        public uint SwapchainCount;
        public ulong* PPresentIds;

        public static VkPresentIdKHR New() => new VkPresentIdKHR { SType = TYPE };
    }

    internal unsafe struct VkPhysicalDevicePresentIdFeaturesKHR
    {
        public const VkStructureType TYPE = (VkStructureType)1000294001;

        public VkStructureType SType;
        public void* PNext;
        public uint PresentId;

        public static VkPhysicalDevicePresentIdFeaturesKHR New() => new VkPhysicalDevicePresentIdFeaturesKHR { SType = TYPE };
    }

    // VK_KHR_timeline_semaphore

    internal unsafe struct VkPhysicalDeviceTimelineSemaphoreFeaturesKHR
    {
        public const VkStructureType TYPE = (VkStructureType)1000207000;

        public VkStructureType SType;
        public void* PNext;
        public uint TimelineSemaphore;

        public static VkPhysicalDeviceTimelineSemaphoreFeaturesKHR New() => new VkPhysicalDeviceTimelineSemaphoreFeaturesKHR { SType = TYPE };
    }

    internal unsafe struct VkSemaphoreTypeCreateInfoKHR
    {
        public const VkStructureType TYPE = (VkStructureType)1000207002;

        public VkStructureType SType;
        public void* PNext;
        public VkSemaphoreTypeKHR SemaphoreType;
        public ulong InitialValue;

        public static VkSemaphoreTypeCreateInfoKHR New() => new VkSemaphoreTypeCreateInfoKHR { SType = TYPE };
    }

    internal unsafe struct VkSemaphoreWaitInfoKHR
    {
        public const VkStructureType TYPE = (VkStructureType)1000207004;

        public VkStructureType SType;
        public void* PNext;
        public uint Flags;
        public uint SemaphoreCount;
        public VkSemaphore* PSemaphores;
        public ulong* PValues;

        public static VkSemaphoreWaitInfoKHR New() => new VkSemaphoreWaitInfoKHR { SType = TYPE };
    }

    internal unsafe struct VkPhysicalDeviceFeatures2Khr
    {
        public const VkStructureType TYPE = (VkStructureType)1000059000;

        public VkStructureType SType;
        public void* PNext;
        public VkPhysicalDeviceFeatures Features;

        public static VkPhysicalDeviceFeatures2Khr New() => new VkPhysicalDeviceFeatures2Khr { SType = TYPE };
    }

    internal unsafe delegate VkResult VkSetLatencySleepModeNvT(VkDevice device, VkSwapchainKHR swapchain, VkLatencySleepModeInfoNV* pSleepModeInfo);

    internal unsafe delegate VkResult VkLatencySleepNvT(VkDevice device, VkSwapchainKHR swapchain, VkLatencySleepInfoNV* pSleepInfo);

    internal unsafe delegate void VkSetLatencyMarkerNvT(VkDevice device, VkSwapchainKHR swapchain, VkSetLatencyMarkerInfoNV* pLatencyMarkerInfo);

    internal unsafe delegate void VkGetLatencyTimingsNvT(VkDevice device, VkSwapchainKHR swapchain, VkGetLatencyMarkerInfoNV* pLatencyMarkerInfo);

    internal unsafe delegate void VkQueueNotifyOutOfBandNvT(VkQueue queue, VkOutOfBandQueueTypeInfoNV* pQueueTypeInfo);

    internal unsafe delegate VkResult VkWaitSemaphoresKhrT(VkDevice device, VkSemaphoreWaitInfoKHR* pWaitInfo, ulong timeout);

    internal unsafe delegate void VkGetPhysicalDeviceFeatures2T(VkPhysicalDevice physicalDevice, void* features);

    /// <summary>
    /// Entry points for VK_NV_low_latency2 (NVIDIA Reflex) and its dependencies.
    /// </summary>
    internal sealed class VkNvLowLatency2
    {
        public VkSetLatencySleepModeNvT SetLatencySleepMode;
        public VkLatencySleepNvT LatencySleep;
        public VkSetLatencyMarkerNvT SetLatencyMarker;
        public VkGetLatencyTimingsNvT GetLatencyTimings;
        public VkQueueNotifyOutOfBandNvT QueueNotifyOutOfBand;
        public VkWaitSemaphoresKhrT WaitSemaphores;

        public bool IsComplete =>
            SetLatencySleepMode != null
            && LatencySleep != null
            && SetLatencyMarker != null
            && GetLatencyTimings != null
            && WaitSemaphores != null;
    }
}
