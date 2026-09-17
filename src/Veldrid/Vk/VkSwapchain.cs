using System;
using System.Linq;
using System.Threading;
using Vulkan;
using static Veldrid.Vk.VulkanUtil;
using static Vulkan.VulkanNative;

namespace Veldrid.Vk
{
    internal unsafe class VkSwapchain : Swapchain
    {
        public override Framebuffer Framebuffer => framebuffer;

        public override bool IsDisposed => disposed;

        public VkSwapchainKHR DeviceSwapchain => deviceSwapchain;
        public uint ImageIndex => currentImageIndex;
        public Vulkan.VkFence ImageAvailableFence => imageAvailableFence;
        public VkSurfaceKHR Surface { get; }

        public VkQueue PresentQueue => presentQueue;
        public uint PresentQueueIndex => presentQueueIndex;
        public ResourceRefCount RefCount { get; }

        public override string Name
        {
            get => name;
            set
            {
                name = value;
                gd.SetResourceName(this, value);
            }
        }

        public override bool SyncToVerticalBlank
        {
            get => newSyncToVBlank ?? syncToVBlank;
            set
            {
                if (syncToVBlank != value) newSyncToVBlank = value;
            }
        }

        private bool allowTearing;

        public bool AllowTearing
        {
            get => allowTearing;
            set
            {
                if (allowTearing == value)
                    return;

                allowTearing = value;

                recreateAndReacquire(framebuffer.Width, framebuffer.Height);
            }
        }

        private readonly VkGraphicsDevice gd;
        private readonly VkSwapchainFramebuffer framebuffer;
        private readonly uint presentQueueIndex;
        private readonly VkQueue presentQueue;
        private readonly bool colorSrgb;
        private VkSwapchainKHR deviceSwapchain;
        private Vulkan.VkFence imageAvailableFence;
        private bool syncToVBlank;
        private bool? newSyncToVBlank;
        private uint currentImageIndex;
        private string name;
        private bool disposed;

        // Low latency mode variables. For now only lowlatency2 (NVIDIA Reflex), but will be extended to AMD anti lag in the future
        private VkSemaphore latencySemaphore;
        private ulong latencySemaphoreValue;
        private LowLatencyMode lowLatencyMode;
        private uint lowLatencyMinimumIntervalUs;
        private bool renderSubmitStarted;

        /// <summary>
        /// Whether VK_NV_low_latency2 is usable with this swapchain.
        /// </summary>
        public bool LowLatencySupported => gd.LowLatency != null && latencySemaphore != VkSemaphore.Null;

        // This ID is only incremented in this class, but potentially read from other threads via <see cref="VkGraphicsDevice.SetLatencyMarker"/>.
        // Hence interlocked increment + volatile read to prevent data races that would correlate timings of past with present frames.
        private ulong currentPresentId;

        /// <summary>
        /// The present ID of the frame currently being built, or 0 if no frame has begun.
        /// </summary>
        public ulong CurrentPresentId => Volatile.Read(ref currentPresentId);

        /// <summary>
        /// Low latency mode (CPU work paced against GPU completion).
        /// </summary>
        public LowLatencyMode LowLatencyMode
        {
            get => lowLatencyMode;
            set
            {
                if (lowLatencyMode == value) return;

                lowLatencyMode = value;
                applyLatencySleepMode();
                recreateAndReacquire(framebuffer.Width, framebuffer.Height);
            }
        }

        /// <summary>
        /// Minimum interval between presents, in microseconds. Zero disables the frame cap. Only has an effect
        /// when <see cref="LowLatencyMode"/> is not <see cref="LowLatencyMode.Off"/>.
        /// </summary>
        public uint LowLatencyMinimumIntervalUs
        {
            get => lowLatencyMinimumIntervalUs;
            set
            {
                if (lowLatencyMinimumIntervalUs == value) return;

                lowLatencyMinimumIntervalUs = value;
                applyLatencySleepMode();
            }
        }

        /// <summary>
        /// Begins a new latency frame and blocks until the driver says CPU work should start.
        /// Must be called exactly once between presents, before input sampling.
        /// </summary>
        public void LatencySleep()
        {
            if (!LowLatencySupported) return;

            Interlocked.Increment(ref currentPresentId);
            renderSubmitStarted = false;

            if (lowLatencyMode == LowLatencyMode.Off) return;

            ulong waitValue = ++latencySemaphoreValue;

            var sleepInfo = VkLatencySleepInfoNV.New();
            sleepInfo.SignalSemaphore = latencySemaphore;
            sleepInfo.Value = waitValue;

            var result = gd.LowLatency.LatencySleep(gd.Device, deviceSwapchain, &sleepInfo);
            if (result != VkResult.Success) return;

            var semaphore = latencySemaphore;

            var waitInfo = VkSemaphoreWaitInfoKHR.New();
            waitInfo.SemaphoreCount = 1;
            waitInfo.PSemaphores = &semaphore;
            waitInfo.PValues = &waitValue;

            gd.LowLatency.WaitSemaphores(gd.Device, &waitInfo, ulong.MaxValue);
        }

        public void SetLatencyMarker(VkLatencyMarkerNV marker)
        {
            if (!LowLatencySupported || CurrentPresentId == 0) return;

            var info = VkSetLatencyMarkerInfoNV.New();
            info.PresentID = CurrentPresentId;
            info.Marker = marker;

            gd.LowLatency.SetLatencyMarker(gd.Device, deviceSwapchain, &info);
        }

        /// <summary>
        /// Emits the simulation-end / render-submit-start pair on the first submission of a frame.
        /// </summary>
        public void NotifyCommandBufferSubmitting()
        {
            if (!LowLatencySupported || CurrentPresentId == 0 || renderSubmitStarted) return;

            renderSubmitStarted = true;
            SetLatencyMarker(VkLatencyMarkerNV.SimulationEnd);
            SetLatencyMarker(VkLatencyMarkerNV.RenderSubmitStart);
        }

        public void NotifyRenderSubmitEnd()
        {
            if (!renderSubmitStarted) return;

            renderSubmitStarted = false;
            SetLatencyMarker(VkLatencyMarkerNV.RenderSubmitEnd);
        }

        /// <summary>
        /// Retrieves the most recent frame reports collected by the driver.
        /// </summary>
        public VkLatencyTimingsFrameReportNV[] GetLatencyTimings()
        {
            if (!LowLatencySupported) return [];

            var info = VkGetLatencyMarkerInfoNV.New();
            gd.LowLatency.GetLatencyTimings(gd.Device, deviceSwapchain, &info);

            uint count = info.TimingCount;
            if (count == 0) return [];

            var timings = new VkLatencyTimingsFrameReportNV[count];
            for (int i = 0; i < timings.Length; i++) timings[i] = VkLatencyTimingsFrameReportNV.New();

            fixed (VkLatencyTimingsFrameReportNV* ptr = timings)
            {
                info.PTimings = ptr;
                gd.LowLatency.GetLatencyTimings(gd.Device, deviceSwapchain, &info);
            }

            if (info.TimingCount == count) return timings;

            var trimmed = new VkLatencyTimingsFrameReportNV[info.TimingCount];
            Array.Copy(timings, trimmed, trimmed.Length);
            return trimmed;
        }

        private void applyLatencySleepMode()
        {
            if (!LowLatencySupported) return;

            var modeInfo = VkLatencySleepModeInfoNV.New();
            modeInfo.LowLatencyMode = lowLatencyMode != LowLatencyMode.Off ? 1u : 0u;
            modeInfo.LowLatencyBoost = lowLatencyMode == LowLatencyMode.OnWithBoost ? 1u : 0u;
            modeInfo.MinimumIntervalUs = lowLatencyMinimumIntervalUs;

            gd.LowLatency.SetLatencySleepMode(gd.Device, deviceSwapchain, &modeInfo);
        }

        private void createLatencySemaphore()
        {
            if (gd.LowLatency == null) return;

            var typeCi = VkSemaphoreTypeCreateInfoKHR.New();
            typeCi.SemaphoreType = VkSemaphoreTypeKHR.Timeline;
            typeCi.InitialValue = 0;

            var semaphoreCi = VkSemaphoreCreateInfo.New();
            semaphoreCi.pNext = &typeCi;

            var result = vkCreateSemaphore(gd.Device, ref semaphoreCi, null, out latencySemaphore);
            if (result != VkResult.Success) latencySemaphore = VkSemaphore.Null;
        }

        public VkSwapchain(VkGraphicsDevice gd, ref SwapchainDescription description)
            : this(gd, ref description, VkSurfaceKHR.Null)
        {
        }

        public VkSwapchain(VkGraphicsDevice gd, ref SwapchainDescription description, VkSurfaceKHR existingSurface)
        {
            this.gd = gd;
            syncToVBlank = description.SyncToVerticalBlank;
            colorSrgb = description.ColorSrgb;

            SwapchainSource swapchainSource = description.Source;

            Surface = existingSurface == VkSurfaceKHR.Null
                ? VkSurfaceUtil.CreateSurface(gd, gd.Instance, swapchainSource)
                : existingSurface;

            if (!getPresentQueueIndex(out presentQueueIndex)) throw new VeldridException("The system does not support presenting the given Vulkan surface.");

            vkGetDeviceQueue(this.gd.Device, presentQueueIndex, 0, out presentQueue);

            createLatencySemaphore();

            framebuffer = new VkSwapchainFramebuffer(gd, this, Surface, description.Width, description.Height, description.DepthFormat);

            createSwapchain(description.Width, description.Height);

            var fenceCi = VkFenceCreateInfo.New();
            fenceCi.flags = VkFenceCreateFlags.None;
            vkCreateFence(this.gd.Device, ref fenceCi, null, out imageAvailableFence);

            AcquireNextImage(this.gd.Device, VkSemaphore.Null, imageAvailableFence);
            vkWaitForFences(this.gd.Device, 1, ref imageAvailableFence, true, ulong.MaxValue);
            vkResetFences(this.gd.Device, 1, ref imageAvailableFence);

            RefCount = new ResourceRefCount(disposeCore);
        }

        #region Disposal

        public override void Dispose()
        {
            RefCount.Decrement();
        }

        #endregion

        public override void Resize(uint width, uint height)
        {
            recreateAndReacquire(width, height);
        }

        public bool AcquireNextImage(VkDevice device, VkSemaphore semaphore, Vulkan.VkFence fence)
        {
            if (newSyncToVBlank != null)
            {
                syncToVBlank = newSyncToVBlank.Value;
                newSyncToVBlank = null;
                recreateAndReacquire(framebuffer.Width, framebuffer.Height);
                return false;
            }

            var result = vkAcquireNextImageKHR(
                device,
                deviceSwapchain,
                ulong.MaxValue,
                semaphore,
                fence,
                ref currentImageIndex);
            framebuffer.SetImageIndex(currentImageIndex);

            if (result == VkResult.ErrorOutOfDateKHR || result == VkResult.SuboptimalKHR)
            {
                createSwapchain(framebuffer.Width, framebuffer.Height);
                return false;
            }

            if (result != VkResult.Success) throw new VeldridException("Could not acquire next image from the Vulkan swapchain.");

            return true;
        }

        private void recreateAndReacquire(uint width, uint height)
        {
            if (createSwapchain(width, height))
            {
                if (AcquireNextImage(gd.Device, VkSemaphore.Null, imageAvailableFence))
                {
                    vkWaitForFences(gd.Device, 1, ref imageAvailableFence, true, ulong.MaxValue);
                    vkResetFences(gd.Device, 1, ref imageAvailableFence);
                }
            }
        }

        private bool createSwapchain(uint width, uint height)
        {
            // Obtain the surface capabilities first -- this will indicate whether the surface has been lost.
            var result = vkGetPhysicalDeviceSurfaceCapabilitiesKHR(gd.PhysicalDevice, Surface, out var surfaceCapabilities);
            if (result == VkResult.ErrorSurfaceLostKHR) throw new VeldridException("The Swapchain's underlying surface has been lost.");

            if (surfaceCapabilities.minImageExtent.width == 0 && surfaceCapabilities.minImageExtent.height == 0
                                                              && surfaceCapabilities.maxImageExtent.width == 0 && surfaceCapabilities.maxImageExtent.height == 0)
                return false;

            if (deviceSwapchain != VkSwapchainKHR.Null) gd.WaitForIdle();

            currentImageIndex = 0;
            uint surfaceFormatCount = 0;
            result = vkGetPhysicalDeviceSurfaceFormatsKHR(gd.PhysicalDevice, Surface, ref surfaceFormatCount, null);
            CheckResult(result);
            var formats = new VkSurfaceFormatKHR[surfaceFormatCount];
            result = vkGetPhysicalDeviceSurfaceFormatsKHR(gd.PhysicalDevice, Surface, ref surfaceFormatCount, out formats[0]);
            CheckResult(result);

            var desiredFormat = colorSrgb
                ? VkFormat.B8g8r8a8Srgb
                : VkFormat.B8g8r8a8Unorm;

            var surfaceFormat = new VkSurfaceFormatKHR();

            if (formats.Length == 1 && formats[0].format == VkFormat.Undefined)
                surfaceFormat = new VkSurfaceFormatKHR { colorSpace = VkColorSpaceKHR.SrgbNonlinearKHR, format = desiredFormat };
            else
            {
                foreach (var format in formats)
                {
                    if (format.colorSpace == VkColorSpaceKHR.SrgbNonlinearKHR && format.format == desiredFormat)
                    {
                        surfaceFormat = format;
                        break;
                    }
                }

                if (surfaceFormat.format == VkFormat.Undefined)
                {
                    if (colorSrgb && surfaceFormat.format != VkFormat.R8g8b8a8Srgb) throw new VeldridException("Unable to create an sRGB Swapchain for this surface.");

                    surfaceFormat = formats[0];
                }
            }

            uint presentModeCount = 0;
            result = vkGetPhysicalDeviceSurfacePresentModesKHR(gd.PhysicalDevice, Surface, ref presentModeCount, null);
            CheckResult(result);
            var presentModes = new VkPresentModeKHR[presentModeCount];
            result = vkGetPhysicalDeviceSurfacePresentModesKHR(gd.PhysicalDevice, Surface, ref presentModeCount, out presentModes[0]);
            CheckResult(result);

            var presentMode = VkPresentModeKHR.FifoKHR;

            if (syncToVBlank)
            {
                if (allowTearing && presentModes.Contains(VkPresentModeKHR.FifoRelaxedKHR))
                    presentMode = VkPresentModeKHR.FifoRelaxedKHR;

                // FifoRelaxed's tearing undermines undershooting vrr frame rate caps; FIFO actually yields better latency in that case
                if (lowLatencyMode != LowLatencyMode.Off)
                    presentMode = VkPresentModeKHR.FifoKHR;
            }
            else if (allowTearing && presentModes.Contains(VkPresentModeKHR.ImmediateKHR))
                presentMode = VkPresentModeKHR.ImmediateKHR;
            else if (presentModes.Contains(VkPresentModeKHR.MailboxKHR))
                presentMode = VkPresentModeKHR.MailboxKHR;
            else if (presentModes.Contains(VkPresentModeKHR.ImmediateKHR))
                presentMode = VkPresentModeKHR.ImmediateKHR;

            uint maxImageCount = surfaceCapabilities.maxImageCount == 0 ? uint.MaxValue : surfaceCapabilities.maxImageCount;
            uint imageCount = Math.Min(maxImageCount, surfaceCapabilities.minImageCount + 1);

            var swapchainCi = VkSwapchainCreateInfoKHR.New();
            swapchainCi.surface = Surface;
            swapchainCi.presentMode = presentMode;
            swapchainCi.imageFormat = surfaceFormat.format;
            swapchainCi.imageColorSpace = surfaceFormat.colorSpace;
            uint clampedWidth = Util.Clamp(width, surfaceCapabilities.minImageExtent.width, surfaceCapabilities.maxImageExtent.width);
            uint clampedHeight = Util.Clamp(height, surfaceCapabilities.minImageExtent.height, surfaceCapabilities.maxImageExtent.height);
            swapchainCi.imageExtent = new VkExtent2D { width = clampedWidth, height = clampedHeight };
            swapchainCi.minImageCount = imageCount;
            swapchainCi.imageArrayLayers = 1;
            swapchainCi.imageUsage = VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransferDst;

            var queueFamilyIndices = new FixedArray2<uint>(gd.GraphicsQueueIndex, gd.PresentQueueIndex);

            if (gd.GraphicsQueueIndex != gd.PresentQueueIndex)
            {
                swapchainCi.imageSharingMode = VkSharingMode.Concurrent;
                swapchainCi.queueFamilyIndexCount = 2;
                swapchainCi.pQueueFamilyIndices = &queueFamilyIndices.First;
            }
            else
            {
                swapchainCi.imageSharingMode = VkSharingMode.Exclusive;
                swapchainCi.queueFamilyIndexCount = 0;
            }

            swapchainCi.preTransform = VkSurfaceTransformFlagsKHR.IdentityKHR;
            swapchainCi.compositeAlpha = VkCompositeAlphaFlagsKHR.OpaqueKHR;
            swapchainCi.clipped = true;

            var oldSwapchain = deviceSwapchain;
            swapchainCi.oldSwapchain = oldSwapchain;

            var latencyCi = VkSwapchainLatencyCreateInfoNV.New();

            if (gd.LowLatency != null)
            {
                latencyCi.LatencyModeEnable = 1;
                swapchainCi.pNext = &latencyCi;
            }

            result = vkCreateSwapchainKHR(gd.Device, ref swapchainCi, null, out deviceSwapchain);
            CheckResult(result);
            if (oldSwapchain != VkSwapchainKHR.Null) vkDestroySwapchainKHR(gd.Device, oldSwapchain, null);

            applyLatencySleepMode();

            framebuffer.SetNewSwapchain(deviceSwapchain, width, height, surfaceFormat, swapchainCi.imageExtent);
            return true;
        }

        private bool getPresentQueueIndex(out uint queueFamilyIndex)
        {
            uint deviceGraphicsQueueIndex = gd.GraphicsQueueIndex;
            uint devicePresentQueueIndex = gd.PresentQueueIndex;

            if (queueSupportsPresent(deviceGraphicsQueueIndex, Surface))
            {
                queueFamilyIndex = deviceGraphicsQueueIndex;
                return true;
            }

            if (deviceGraphicsQueueIndex != devicePresentQueueIndex && queueSupportsPresent(devicePresentQueueIndex, Surface))
            {
                queueFamilyIndex = devicePresentQueueIndex;
                return true;
            }

            queueFamilyIndex = 0;
            return false;
        }

        private bool queueSupportsPresent(uint queueFamilyIndex, VkSurfaceKHR surface)
        {
            var result = vkGetPhysicalDeviceSurfaceSupportKHR(
                gd.PhysicalDevice,
                queueFamilyIndex,
                surface,
                out var supported);
            CheckResult(result);
            return supported;
        }

        private void disposeCore()
        {
            if (latencySemaphore != VkSemaphore.Null) vkDestroySemaphore(gd.Device, latencySemaphore, null);

            vkDestroyFence(gd.Device, imageAvailableFence, null);
            framebuffer.Dispose();
            vkDestroySwapchainKHR(gd.Device, deviceSwapchain, null);
            vkDestroySurfaceKHR(gd.Instance, Surface, null);

            disposed = true;
        }
    }
}
