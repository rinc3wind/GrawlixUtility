using System.Runtime.InteropServices;

namespace GrawlixUtility;

internal sealed record AudioDeviceInfo(string Id, string Name);
internal readonly record struct AudioEndpointVolumeState(string Id, bool Muted, float Volume);

internal static class AudioDeviceNames
{
    private static readonly Guid DeviceEnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid DeviceEnumeratorInterfaceId = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid DeviceInterfaceId = new("D666063F-1587-4E43-81F1-B948E807363F");
    private static readonly Guid PropertyStoreInterfaceId = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly Guid EndpointVolumeInterfaceId = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly PropertyKey DeviceFriendlyNameKey = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    public static AudioDeviceInfo? GetDefaultCaptureDevice(AudioDeviceRole role)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IPropertyStore? propertyStore = null;

        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(DeviceEnumeratorClassId, throwOnError: true)!;
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role, out device));

            Marshal.ThrowExceptionForHR(device.GetId(out var id));
            Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0, out propertyStore));
            var key = DeviceFriendlyNameKey;
            Marshal.ThrowExceptionForHR(propertyStore.GetValue(ref key, out var value));
            try
            {
                if (value.Type != VariantType.String || value.Pointer == IntPtr.Zero) return null;
                var name = Marshal.PtrToStringUni(value.Pointer);
                return string.IsNullOrWhiteSpace(name) ? null : new AudioDeviceInfo(id, name);
            }
            finally { PropVariantClear(ref value); }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (propertyStore is not null && Marshal.IsComObject(propertyStore)) Marshal.FinalReleaseComObject(propertyStore);
            if (device is not null && Marshal.IsComObject(device)) Marshal.FinalReleaseComObject(device);
            if (enumerator is not null && Marshal.IsComObject(enumerator)) Marshal.FinalReleaseComObject(enumerator);
        }
    }

    public static IReadOnlyList<AudioEndpointVolumeState> GetDefaultCaptureEndpointVolumes()
    {
        IMMDeviceEnumerator? enumerator = null;
        var endpoints = new List<AudioEndpointVolumeState>();
        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(DeviceEnumeratorClassId, throwOnError: true)!;
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var role in new[] { AudioDeviceRole.Multimedia, AudioDeviceRole.Communications })
            {
                IMMDevice? device = null;
                IAudioEndpointVolume? volume = null;
                try
                {
                    if (enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role, out device) < 0 || device is null) continue;
                    if (device.GetId(out var id) < 0 || !seenIds.Add(id)) continue;
                    volume = ActivateEndpointVolume(device);
                    Marshal.ThrowExceptionForHR(volume.GetMute(out var muted));
                    Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out var scalar));
                    endpoints.Add(new AudioEndpointVolumeState(id, muted, scalar));
                }
                catch { }
                finally
                {
                    if (volume is not null && Marshal.IsComObject(volume)) Marshal.FinalReleaseComObject(volume);
                    if (device is not null && Marshal.IsComObject(device)) Marshal.FinalReleaseComObject(device);
                }
            }
        }
        catch { }
        finally
        {
            if (enumerator is not null && Marshal.IsComObject(enumerator)) Marshal.FinalReleaseComObject(enumerator);
        }
        return endpoints;
    }

    public static void SetCaptureEndpointVolume(string id, float scalar)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? volume = null;
        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(DeviceEnumeratorClassId, throwOnError: true)!;
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(id, out device));
            volume = ActivateEndpointVolume(device);
            var eventContext = Guid.Empty;
            Marshal.ThrowExceptionForHR(volume.SetMasterVolumeLevelScalar(Math.Clamp(scalar, 0f, 1f), ref eventContext));
        }
        finally
        {
            if (volume is not null && Marshal.IsComObject(volume)) Marshal.FinalReleaseComObject(volume);
            if (device is not null && Marshal.IsComObject(device)) Marshal.FinalReleaseComObject(device);
            if (enumerator is not null && Marshal.IsComObject(enumerator)) Marshal.FinalReleaseComObject(enumerator);
        }
    }

    private static IAudioEndpointVolume ActivateEndpointVolume(IMMDevice device)
    {
        var interfaceId = EndpointVolumeInterfaceId;
        Marshal.ThrowExceptionForHR(device.Activate(ref interfaceId, 23, IntPtr.Zero, out var instance));
        try { return (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(instance); }
        finally { Marshal.Release(instance); }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    private enum DataFlow { Render = 0, Capture = 1, All = 2 }
    internal enum AudioDeviceRole { Console = 0, Multimedia = 1, Communications = 2 }
    private enum VariantType : ushort { String = 31 }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PropVariant
    {
        [FieldOffset(0)] public VariantType Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(DataFlow dataFlow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(DataFlow dataFlow, AudioDeviceRole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid interfaceId, uint classContext, IntPtr activationParameters, out IntPtr instance);
        [PreserveSig] int OpenPropertyStore(uint accessMode, out IPropertyStore propertyStore);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int GetChannelCount(out uint channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid eventContext);
        [PreserveSig] int VolumeStepDown(ref Guid eventContext);
        [PreserveSig] int QueryHardwareSupport(out uint hardwareSupportMask);
        [PreserveSig] int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
}
