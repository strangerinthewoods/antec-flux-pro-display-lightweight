using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Configuration;

namespace FluxProDisplay;

public class HardwareMonitor : IDisposable
{
    private const string PipeName = "FluxProDisplay_HardwareMonitor";
    private readonly object _superviseLock = new();
    private Process? _helperProcess;
    private NamedPipeClientStream? _pipeStream;
    private StreamWriter? _writer;
    private StreamReader? _reader;

    // exponential backoff state
    private int _restartAttempts = 0;
    private readonly int _baseBackoffMs;
    private readonly int _maxBackoffMs;

    public HardwareMonitor()
    {
        // configurable backoff: environment variables take precedence, then appsettings.json
        _baseBackoffMs = 500;
        _maxBackoffMs = 300_000;

        try
        {
            var envBase = Environment.GetEnvironmentVariable("FLUX_HELPER_BACKOFF_BASE_MS");
            var envMax = Environment.GetEnvironmentVariable("FLUX_HELPER_BACKOFF_MAX_MS");

            if (!string.IsNullOrEmpty(envBase) && int.TryParse(envBase, out var eb)) _baseBackoffMs = Math.Max(1, eb);
            if (!string.IsNullOrEmpty(envMax) && int.TryParse(envMax, out var em)) _maxBackoffMs = Math.Max(1, em);

            // attempt to read appsettings.json if present (only used when env vars are not set)
            var builder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

            var config = builder.Build();

            if (envBase == null)
            {
                var cfgBase = config.GetValue<int?>("AppSettings:HelperBackoffBaseMs");
                if (cfgBase.HasValue) _baseBackoffMs = Math.Max(1, cfgBase.Value);
            }

            if (envMax == null)
            {
                var cfgMax = config.GetValue<int?>("AppSettings:HelperBackoffMaxMs");
                if (cfgMax.HasValue) _maxBackoffMs = Math.Max(1, cfgMax.Value);
            }
        }
        catch
        {
            // best-effort; keep defaults on any failure
        }

        EnsureHelperRunning();
    }

    public void Dispose()
    {
        lock (_superviseLock)
        {
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _pipeStream?.Dispose(); } catch { }

            if (_helperProcess != null && !_helperProcess.HasExited)
            {
                try
                {
                    _helperProcess.Kill(entireProcessTree: true);
                    _helperProcess.WaitForExit(2000);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex);
                }
                finally
                {
                    _helperProcess.Dispose();
                    _helperProcess = null;
                }
            }
        }

        GC.SuppressFinalize(this);
    }

    public float? GetCpuTemperature()
    {
        var (cpu, _) = GetTempsWithRestart();
        return cpu;
    }

    public float? GetGpuTemperature()
    {
        var (_, gpu) = GetTempsWithRestart();
        return gpu;
    }

    private (float? cpu, float? gpu) GetTempsWithRestart()
    {
        try
        {
            return QueryHelperForTemps();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
            // restart and retry once
            RestartHelper();
            try
            {
                return QueryHelperForTemps();
            }
            catch (Exception retryEx)
            {
                Logger.LogError(retryEx);
                return (null, null);
            }
        }
    }

    private (float? cpu, float? gpu) QueryHelperForTemps()
    {
        lock (_superviseLock)
        {
            EnsureHelperRunning();

            if (_pipeStream == null || _writer == null || _reader == null)
                throw new InvalidOperationException("IPC not initialized.");

            _writer.WriteLine("GET");
            _writer.Flush();

            var response = _reader.ReadLine();
            if (string.IsNullOrEmpty(response)) throw new IOException("Empty response from helper.");

            var parts = response.Split(';');
            if (parts.Length != 2) throw new IOException("Malformed response.");

            float? cpu = ParseNullableFloat(parts[0]);
            float? gpu = ParseNullableFloat(parts[1]);

            return (cpu, gpu);
        }
    }

    private static float? ParseNullableFloat(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
        return float.TryParse(s, System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (float?)null;
    }

    private void EnsureHelperRunning()
    {
        lock (_superviseLock)
        {
            if (_helperProcess != null && !_helperProcess.HasExited && _pipeStream != null && _pipeStream.IsConnected)
            {
                return;
            }

            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _pipeStream?.Dispose(); } catch { }

            _writer = null;
            _reader = null;
            _pipeStream = null;

            var helperExe = Path.Combine(AppContext.BaseDirectory, "FluxProDisplay.HardwareMonitorHelper.exe");
            if (!File.Exists(helperExe))
            {
                var ex = new FileNotFoundException("Hardware monitor helper executable not found.", helperExe);
                Logger.LogError(ex);
                throw ex;
            }

            var psi = new ProcessStartInfo
            {
                FileName = helperExe,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };

            try
            {
                _helperProcess = Process.Start(psi);
                Logger.LogError(new Exception($"Started helper process: PID {_helperProcess?.Id}"));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
                throw;
            }

            var stream = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None);
            try
            {
                stream.Connect(2000); // 2s timeout
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
                try { _helperProcess?.Kill(); } catch { }
                _helperProcess?.Dispose();
                _helperProcess = null;
                throw;
            }

            _pipeStream = stream;
            _reader = new StreamReader(_pipeStream);
            _writer = new StreamWriter(_pipeStream) { AutoFlush = true };

            // Successful start/connection -> reset backoff attempts
            _restartAttempts = 0;
            Logger.LogError(new Exception("Connected to helper named pipe."));
        }
    }

    private void RestartHelper()
    {
        lock (_superviseLock)
        {
            try
            {
                if (_helperProcess != null && !_helperProcess.HasExited)
                {
                    try
                    {
                        _helperProcess.Kill(entireProcessTree: true);
                        _helperProcess.WaitForExit(2000);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex);
                    }
                    finally
                    {
                        _helperProcess.Dispose();
                        _helperProcess = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }

            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _pipeStream?.Dispose(); } catch { }

            _writer = null;
            _reader = null;
            _pipeStream = null;

            // increment attempts and compute backoff
            _restartAttempts++;
            var delay = ComputeBackoffDelay(_restartAttempts);
            Logger.LogError(new Exception($"Restarting helper process. Attempt {_restartAttempts}, delaying {delay}ms before restart."));
            try
            {
                // sleep while holding lock to prevent concurrent restarts
                Thread.Sleep(delay);
            }
            catch (ThreadInterruptedException) { }

            EnsureHelperRunning();
        }
    }

    private int ComputeBackoffDelay(int attempts)
    {
        try
        {
            // exponential backoff: base * 2^(attempts-1), capped
            var multiplier = 1L << Math.Min(attempts - 1, 30); // avoid overflow
            var delay = Math.Min(_baseBackoffMs * multiplier, _maxBackoffMs);
            return (int)delay;
        }
        catch
        {
            return _maxBackoffMs;
        }
    }
}