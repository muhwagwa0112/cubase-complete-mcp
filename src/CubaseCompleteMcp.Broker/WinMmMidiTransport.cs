using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using CubaseCompleteMcp.Core;
using Microsoft.Extensions.Logging;

namespace CubaseCompleteMcp.Broker;

public sealed class WinMmMidiTransport : IAsyncDisposable
{
    private const uint CallbackFunction = 0x00030000;
    private const uint MimLongData = 0x3C4;
    private const uint MomDone = 0x3C9;
    private const int InputBufferBytes = 64 * 1024;
    private readonly ILogger<WinMmMidiTransport> _logger;
    private readonly MidiInProc _inputCallback;
    private readonly MidiOutProc _outputCallback;
    private readonly ConcurrentDictionary<IntPtr, TaskCompletionSource<bool>> _outputCompletions = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private IntPtr _inputHandle;
    private IntPtr _outputHandle;
    private IntPtr _inputHeader;
    private IntPtr _inputBuffer;
    private bool _disposed;

    public WinMmMidiTransport(ILogger<WinMmMidiTransport> logger)
    {
        _logger = logger;
        _inputCallback = OnMidiInput;
        _outputCallback = OnMidiOutput;
    }

    public event EventHandler<byte[]>? MessageReceived;
    public bool IsOpen => _inputHandle != IntPtr.Zero && _outputHandle != IntPtr.Zero;
    public string? LastError { get; private set; }

    public IReadOnlyList<MidiPortSnapshot> EnumeratePorts()
    {
        var ports = new List<MidiPortSnapshot>();
        for (uint index = 0; index < midiInGetNumDevs(); index++)
        {
            if (midiInGetDevCapsW((UIntPtr)index, out var caps, (uint)Marshal.SizeOf<MidiInCaps>()) == 0)
            {
                ports.Add(new MidiPortSnapshot(index, caps.Name, true));
            }
        }

        for (uint index = 0; index < midiOutGetNumDevs(); index++)
        {
            if (midiOutGetDevCapsW((UIntPtr)index, out var caps, (uint)Marshal.SizeOf<MidiOutCaps>()) == 0)
            {
                ports.Add(new MidiPortSnapshot(index, caps.Name, false));
            }
        }

        return ports;
    }

    public bool TryOpen(string inputPortName, string outputPortName)
    {
        ThrowIfDisposed();
        if (IsOpen)
        {
            return true;
        }

        LastError = null;
        var ports = EnumeratePorts();
        var input = ports.SingleOrDefault(port => port.IsInput && port.Name.Equals(inputPortName, StringComparison.OrdinalIgnoreCase));
        var output = ports.SingleOrDefault(port => !port.IsInput && port.Name.Equals(outputPortName, StringComparison.OrdinalIgnoreCase));
        if (input is null || output is null)
        {
            LastError = $"Required loopMIDI ports were not found. Input='{inputPortName}', output='{outputPortName}'.";
            return false;
        }

        try
        {
            Check(midiInOpen(out _inputHandle, input.Id, _inputCallback, IntPtr.Zero, CallbackFunction), "midiInOpen");
            Check(midiOutOpen(out _outputHandle, output.Id, _outputCallback, IntPtr.Zero, CallbackFunction), "midiOutOpen");
            PrepareInputBuffer();
            Check(midiInStart(_inputHandle), "midiInStart");
            return true;
        }
        catch (Exception error)
        {
            LastError = error.Message;
            _logger.LogWarning(error, "Could not open Cubase MIDI ports.");
            CloseHandles();
            return false;
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!IsOpen)
        {
            throw new InvalidOperationException("MIDI transport is not open.");
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IntPtr data = IntPtr.Zero;
        IntPtr headerPointer = IntPtr.Zero;
        var prepared = false;
        try
        {
            data = Marshal.AllocHGlobal(message.Length);
            Marshal.Copy(message.ToArray(), 0, data, message.Length);
            var header = CreateHeader(data, message.Length);
            headerPointer = Marshal.AllocHGlobal(Marshal.SizeOf<MidiHeader>());
            Marshal.StructureToPtr(header, headerPointer, false);
            Check(midiOutPrepareHeader(_outputHandle, headerPointer, (uint)Marshal.SizeOf<MidiHeader>()), "midiOutPrepareHeader");
            prepared = true;

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _outputCompletions[headerPointer] = completion;
            Check(midiOutLongMsg(_outputHandle, headerPointer, (uint)Marshal.SizeOf<MidiHeader>()), "midiOutLongMsg");
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            Check(midiOutUnprepareHeader(_outputHandle, headerPointer, (uint)Marshal.SizeOf<MidiHeader>()), "midiOutUnprepareHeader");
            prepared = false;
        }
        catch
        {
            if (_outputHandle != IntPtr.Zero)
            {
                _ = midiOutReset(_outputHandle);
                if (prepared && headerPointer != IntPtr.Zero)
                {
                    _ = midiOutUnprepareHeader(_outputHandle, headerPointer, (uint)Marshal.SizeOf<MidiHeader>());
                }
            }

            throw;
        }
        finally
        {
            if (headerPointer != IntPtr.Zero)
            {
                _outputCompletions.TryRemove(headerPointer, out _);
                Marshal.FreeHGlobal(headerPointer);
            }

            if (data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(data);
            }

            _sendGate.Release();
        }
    }

    private void PrepareInputBuffer()
    {
        _inputBuffer = Marshal.AllocHGlobal(InputBufferBytes);
        var header = CreateHeader(_inputBuffer, InputBufferBytes);
        _inputHeader = Marshal.AllocHGlobal(Marshal.SizeOf<MidiHeader>());
        Marshal.StructureToPtr(header, _inputHeader, false);
        Check(midiInPrepareHeader(_inputHandle, _inputHeader, (uint)Marshal.SizeOf<MidiHeader>()), "midiInPrepareHeader");
        Check(midiInAddBuffer(_inputHandle, _inputHeader, (uint)Marshal.SizeOf<MidiHeader>()), "midiInAddBuffer");
    }

    private void OnMidiInput(IntPtr handle, uint message, IntPtr instance, IntPtr parameter1, IntPtr parameter2)
    {
        if (message != MimLongData || parameter1 == IntPtr.Zero || _disposed)
        {
            return;
        }

        try
        {
            var header = Marshal.PtrToStructure<MidiHeader>(parameter1);
            if (header.BytesRecorded > 0 && header.BytesRecorded <= header.BufferLength)
            {
                var data = new byte[header.BytesRecorded];
                Marshal.Copy(header.Data, data, 0, data.Length);
                MessageReceived?.Invoke(this, data);
            }

            header.BytesRecorded = 0;
            Marshal.StructureToPtr(header, parameter1, false);
            _ = midiInAddBuffer(handle, parameter1, (uint)Marshal.SizeOf<MidiHeader>());
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Failed to process incoming Cubase SysEx.");
        }
    }

    private void OnMidiOutput(IntPtr handle, uint message, IntPtr instance, IntPtr parameter1, IntPtr parameter2)
    {
        if (message == MomDone && parameter1 != IntPtr.Zero && _outputCompletions.TryGetValue(parameter1, out var completion))
        {
            completion.TrySetResult(true);
        }
    }

    private static MidiHeader CreateHeader(IntPtr data, int length) => new()
    {
        Data = data,
        BufferLength = checked((uint)length),
        BytesRecorded = 0,
        Reserved = new IntPtr[8]
    };

    private static void Check(uint result, string operation)
    {
        if (result != 0)
        {
            throw new Win32Exception((int)result, $"{operation} failed with MMRESULT {result}.");
        }
    }

    private void CloseHandles()
    {
        if (_inputHandle != IntPtr.Zero)
        {
            _ = midiInStop(_inputHandle);
            _ = midiInReset(_inputHandle);
            if (_inputHeader != IntPtr.Zero)
            {
                _ = midiInUnprepareHeader(_inputHandle, _inputHeader, (uint)Marshal.SizeOf<MidiHeader>());
            }
            _ = midiInClose(_inputHandle);
            _inputHandle = IntPtr.Zero;
        }

        if (_outputHandle != IntPtr.Zero)
        {
            _ = midiOutReset(_outputHandle);
            _ = midiOutClose(_outputHandle);
            _outputHandle = IntPtr.Zero;
        }

        if (_inputHeader != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_inputHeader);
            _inputHeader = IntPtr.Zero;
        }

        if (_inputBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_inputBuffer);
            _inputBuffer = IntPtr.Zero;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            CloseHandles();
            _sendGate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MidiInCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Support;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MidiOutCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public ushort Technology;
        public ushort Voices;
        public ushort Notes;
        public ushort ChannelMask;
        public uint Support;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MidiHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public IntPtr Next;
        public IntPtr ReservedPointer;
        public uint Offset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public IntPtr[] Reserved;
    }

    private delegate void MidiInProc(IntPtr handle, uint message, IntPtr instance, IntPtr parameter1, IntPtr parameter2);
    private delegate void MidiOutProc(IntPtr handle, uint message, IntPtr instance, IntPtr parameter1, IntPtr parameter2);

    [DllImport("winmm.dll")]
    private static extern uint midiInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint midiInGetDevCapsW(UIntPtr deviceId, out MidiInCaps caps, uint size);
    [DllImport("winmm.dll")]
    private static extern uint midiOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint midiOutGetDevCapsW(UIntPtr deviceId, out MidiOutCaps caps, uint size);
    [DllImport("winmm.dll")]
    private static extern uint midiInOpen(out IntPtr handle, uint deviceId, MidiInProc callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")]
    private static extern uint midiInPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")]
    private static extern uint midiInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")]
    private static extern uint midiInAddBuffer(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")]
    private static extern uint midiInStart(IntPtr handle);
    [DllImport("winmm.dll")]
    private static extern uint midiInStop(IntPtr handle);
    [DllImport("winmm.dll")]
    private static extern uint midiInReset(IntPtr handle);
    [DllImport("winmm.dll")]
    private static extern uint midiInClose(IntPtr handle);
    [DllImport("winmm.dll")]
    private static extern uint midiOutOpen(out IntPtr handle, uint deviceId, MidiOutProc callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")]
    private static extern uint midiOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")]
    private static extern uint midiOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")]
    private static extern uint midiOutLongMsg(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")]
    private static extern uint midiOutReset(IntPtr handle);
    [DllImport("winmm.dll")]
    private static extern uint midiOutClose(IntPtr handle);
}
