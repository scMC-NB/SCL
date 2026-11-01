// SCL Terracotta HTTP/IPC integration.
// Uses Terracotta's documented launcher handoff and local HTTP state endpoints.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SCL.Core;

public enum TerracottaLobbyState
{
    Creating,
    Joining,
    Active,
    Stopping
}

public enum TerracottaPlayerKind
{
    Host,
    Guest
}

public sealed record TerracottaLobbyPlayer(
    string DisplayName,
    string MachineId,
    string Vendor,
    TerracottaPlayerKind Kind,
    bool IsLocal = false);

public sealed record TerracottaLobbySnapshot(
    string RoomCode,
    TerracottaLobbyState State,
    IReadOnlyList<TerracottaLobbyPlayer> Players);

public sealed class TerracottaLobbyException : Exception
{
    public TerracottaLobbyException(string message) : base(message) { }
    public TerracottaLobbyException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class TerracottaMultiplayerClient : IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan HandoffTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly string LockFilePath = Path.Combine(Path.GetTempPath(), "terracotta", "terracotta.lock");
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly SemaphoreSlim operationLock = new(1, 1);
    private LobbyRuntime? runtime;
    private TerracottaLobbySnapshot? current;
    private bool disposed;

    public TerracottaLobbySnapshot? Current => Volatile.Read(ref current);

    public event Action<TerracottaLobbySnapshot>? SnapshotChanged;
    public event Action<string>? Stopped;

    public async Task<TerracottaLobbySnapshot> CreateHostAsync(
        TerracottaModule module,
        string hostName,
        CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (runtime is not null)
                throw new TerracottaLobbyException("已经存在一个联机房间，请先结束当前房间。");

            string normalizedName = CleanText(hostName, "Player", 64);
            LobbyRuntime? created = null;
            try
            {
                Endpoint endpoint = await GetOrStartEndpointAsync(module, cancellationToken).ConfigureAwait(false);
                created = new LobbyRuntime(endpoint.Port, endpoint.OwnedProcess, isHost: true);
                TerracottaState initial = await GetStateAsync(endpoint.Port, cancellationToken).ConfigureAwait(false);
                if (initial.Kind != StateKind.Waiting)
                    throw new TerracottaLobbyException("Terracotta 正在被其他房间使用，请先关闭已有房间。");

                runtime = created;
                Publish(new TerracottaLobbySnapshot(string.Empty, TerracottaLobbyState.Creating, Array.Empty<TerracottaLobbyPlayer>()));
                await SendCommandAsync(endpoint.Port, BuildHostPath(normalizedName), cancellationToken).ConfigureAwait(false);
                created.ControllerStateStarted = true;
                TerracottaLobbySnapshot active = await WaitForHostAsync(created, cancellationToken).ConfigureAwait(false);
                Publish(active);
                created.MonitorTask = MonitorAsync(created);
                return active;
            }
            catch
            {
                await CleanupFailedAsync(created).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<TerracottaLobbySnapshot> JoinAsync(
        TerracottaModule module,
        string roomCode,
        string playerName,
        CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (runtime is not null)
                throw new TerracottaLobbyException("已经存在一个联机房间，请先结束当前房间。");

            string normalizedRoom = CleanText(roomCode, string.Empty, 256);
            if (normalizedRoom.Length == 0)
                throw new TerracottaLobbyException("请先输入房间码。");
            string normalizedName = CleanText(playerName, "Player", 64);

            LobbyRuntime? created = null;
            try
            {
                Endpoint endpoint = await GetOrStartEndpointAsync(module, cancellationToken).ConfigureAwait(false);
                created = new LobbyRuntime(endpoint.Port, endpoint.OwnedProcess, isHost: false);
                TerracottaState initial = await GetStateAsync(endpoint.Port, cancellationToken).ConfigureAwait(false);
                if (initial.Kind != StateKind.Waiting)
                    throw new TerracottaLobbyException("Terracotta 正在被其他房间使用，请先关闭已有房间。");

                runtime = created;
                Publish(new TerracottaLobbySnapshot(normalizedRoom, TerracottaLobbyState.Joining, Array.Empty<TerracottaLobbyPlayer>()));
                await SendCommandAsync(endpoint.Port, BuildGuestPath(normalizedRoom, normalizedName), cancellationToken, invalidRoomIsUserError: true).ConfigureAwait(false);
                created.ControllerStateStarted = true;
                TerracottaLobbySnapshot active = await WaitForGuestAsync(created, normalizedRoom, cancellationToken).ConfigureAwait(false);
                Publish(active);
                created.MonitorTask = MonitorAsync(created);
                return active;
            }
            catch
            {
                await CleanupFailedAsync(created).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        LobbyRuntime? toStop = Volatile.Read(ref runtime);
        toStop?.RequestStop();
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LobbyRuntime? active = runtime;
            if (active is null) return;
            runtime = null;
            TerracottaLobbySnapshot? snapshot = Current;
            if (snapshot is not null) Publish(snapshot with { State = TerracottaLobbyState.Stopping });
            await StopRuntimeAsync(active).ConfigureAwait(false);
            Volatile.Write(ref current, null);
        }
        finally { operationLock.Release(); }
    }

    private async Task<Endpoint> GetOrStartEndpointAsync(TerracottaModule module, CancellationToken ct)
    {
        int? port = TryReadExistingPort();
        if (port is int existingPort && await TryValidateMetaAsync(existingPort, module, false, ct).ConfigureAwait(false))
        {
            return new Endpoint(existingPort, null);
        }

        string handoffPath = Path.Combine(Path.GetTempPath(), $"scl-terracotta-{Guid.NewGuid():N}.json");
        Process? process = null;
        try
        {
            process = StartTerracotta(module, handoffPath);
            int dynamicPort = await WaitForHandoffAsync(process, handoffPath, ct).ConfigureAwait(false);
            bool ownsProcess = await ClassifyOwnershipAsync(process, TimeSpan.FromMilliseconds(750), ct).ConfigureAwait(false);
            if (!await TryValidateMetaAsync(dynamicPort, module, ownsProcess, ct).ConfigureAwait(false))
                throw new TerracottaLobbyException("Terracotta 返回的服务端口或版本信息不兼容。");
            if (!ownsProcess)
            {
                process.Dispose();
                process = null;
            }
            return new Endpoint(dynamicPort, process);
        }
        catch
        {
            if (process is not null) await StopOwnedProcessAsync(process).ConfigureAwait(false);
            throw;
        }
        finally
        {
            TryDeleteFile(handoffPath);
            TryDeleteFile(handoffPath + ".tmp");
        }
    }

    private static Process StartTerracotta(TerracottaModule module, string handoffPath)
    {
        var info = new ProcessStartInfo
        {
            FileName = module.ExecutablePath,
            WorkingDirectory = module.DirectoryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("--hmcl2");
        info.ArgumentList.Add(handoffPath);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new TerracottaLobbyException("无法启动 Terracotta 核心进程。");
        }
        _ = DrainOutputAsync(process.StandardOutput);
        _ = DrainOutputAsync(process.StandardError);
        return process;
    }

    private static async Task<int> WaitForHandoffAsync(Process process, string filePath, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandoffTimeout);
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (File.Exists(filePath))
            {
                try
                {
                    string json = await File.ReadAllTextAsync(filePath, timeout.Token).ConfigureAwait(false);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("port", out JsonElement p) && p.TryGetInt32(out int port) && port is > 0 and <= 65535)
                        return port;
                    throw new InvalidDataException("Terracotta 交接文件里的端口无效。");
                }
                catch (IOException) { await Task.Delay(50, timeout.Token).ConfigureAwait(false); }
                catch (JsonException) { await Task.Delay(50, timeout.Token).ConfigureAwait(false); }
            }
            if (process.HasExited)
                throw new TerracottaLobbyException("Terracotta 尚未提供动态端口就退出了。");
            await Task.Delay(50, timeout.Token).ConfigureAwait(false);
        }
    }

    private static async Task<bool> ClassifyOwnershipAsync(Process process, TimeSpan gracePeriod, CancellationToken ct)
    {
        if (process.HasExited) return false;
        try
        {
            await process.WaitForExitAsync(ct).WaitAsync(gracePeriod, ct).ConfigureAwait(false);
            return false;
        }
        catch (TimeoutException) { return !process.HasExited; }
    }

    private async Task<bool> TryValidateMetaAsync(int port, TerracottaModule module, bool requireExactVersion, CancellationToken ct)
    {
        try
        {
            using JsonDocument doc = await GetJsonAsync(port, "/meta", ct).ConfigureAwait(false);
            JsonElement root = doc.RootElement;
            string? version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
            string? os = root.TryGetProperty("target_os", out var o) ? o.GetString() : null;
            string? arch = root.TryGetProperty("target_arch", out var a) ? a.GetString() : null;
            bool archMatches = module.Architecture switch
            {
                "x86_64" => string.Equals(arch, "x86_64", StringComparison.OrdinalIgnoreCase),
                "arm64" => string.Equals(arch, "arm64", StringComparison.OrdinalIgnoreCase) || string.Equals(arch, "aarch64", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
            if (!string.Equals(os, "windows", StringComparison.OrdinalIgnoreCase) || !archMatches || string.IsNullOrWhiteSpace(version)) return false;
            return !requireExactVersion || string.Equals(version, module.Version, StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task<TerracottaLobbySnapshot> WaitForHostAsync(LobbyRuntime active, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, active.Token);
        timeout.CancelAfter(StartupTimeout);
        StateKind lastState = StateKind.Other;
        string lastPollIssue = string.Empty;
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                EnsureOwnedProcessRunning(active);
                TerracottaState state;
                try
                {
                    state = await GetStateAsync(active.Port, timeout.Token).ConfigureAwait(false);
                    lastPollIssue = string.Empty;
                }
                catch (TimeoutException ex) when (!timeout.IsCancellationRequested)
                {
                    lastPollIssue = ex.Message;
                    await Task.Delay(PollInterval, timeout.Token).ConfigureAwait(false);
                    continue;
                }
                lastState = state.Kind;
                if (state.Kind == StateKind.HostOk)
                {
                    if (string.IsNullOrWhiteSpace(state.RoomCode)) throw new TerracottaLobbyException("Terracotta 没有返回有效房间码。");
                    return new TerracottaLobbySnapshot(state.RoomCode, TerracottaLobbyState.Active, state.Players);
                }
                if (state.Kind == StateKind.Exception) throw new TerracottaLobbyException(MapException(state.ExceptionType));
                if (state.Kind == StateKind.Waiting) throw new TerracottaLobbyException("Terracotta 返回了等待状态，房间未能创建。");
                if (state.Kind is StateKind.GuestConnecting or StateKind.GuestStarting or StateKind.GuestOk or StateKind.Other)
                    throw new TerracottaLobbyException("Terracotta 返回了与创建房间不匹配的状态。");
                await Task.Delay(PollInterval, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !active.Token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            string phase = lastState switch
            {
                StateKind.HostScanning => "正在搜索 Minecraft 局域网世界。请先进入世界并点击“对局域网开放”，再创建房间。",
                StateKind.HostStarting => "已发现局域网世界，但 Terracotta 尚未完成主机初始化。",
                _ => "Terracotta 本地状态接口一直没有返回可用的房主状态。"
            };
            string detail = string.IsNullOrWhiteSpace(lastPollIssue) ? string.Empty : " 最近一次状态请求异常：" + lastPollIssue;
            throw new TimeoutException($"创建房间等待超时（{StartupTimeout.TotalSeconds:0} 秒）。最后状态：{lastState}。{phase}{detail}");
        }
    }

    private async Task<TerracottaLobbySnapshot> WaitForGuestAsync(LobbyRuntime active, string roomCode, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, active.Token);
        timeout.CancelAfter(StartupTimeout);
        string canonicalRoom = roomCode;
        StateKind lastState = StateKind.Other;
        string lastPollIssue = string.Empty;
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                EnsureOwnedProcessRunning(active);

                TerracottaState state;
                try
                {
                    state = await GetStateAsync(active.Port, timeout.Token).ConfigureAwait(false);
                    lastPollIssue = string.Empty;
                }
                catch (TimeoutException ex) when (!timeout.IsCancellationRequested)
                {
                    // A single local HTTP poll timing out must not cancel the whole room join.
                    // The outer startup deadline remains responsible for the final timeout.
                    lastPollIssue = ex.Message;
                    await Task.Delay(PollInterval, timeout.Token).ConfigureAwait(false);
                    continue;
                }

                lastState = state.Kind;
                if ((state.Kind is StateKind.GuestConnecting or StateKind.GuestStarting) && !string.IsNullOrWhiteSpace(state.RoomCode))
                    canonicalRoom = state.RoomCode;
                if (state.Kind == StateKind.GuestOk)
                    return new TerracottaLobbySnapshot(canonicalRoom, TerracottaLobbyState.Active, state.Players);
                if (state.Kind == StateKind.Exception) throw new TerracottaLobbyException(MapException(state.ExceptionType));
                if (state.Kind == StateKind.Waiting) throw new TerracottaLobbyException("Terracotta 返回了等待状态，加入流程已中断。请确认输入的房间码被接受，并重新尝试加入。");
                if (state.Kind is StateKind.HostScanning or StateKind.HostStarting or StateKind.HostOk or StateKind.Other)
                    throw new TerracottaLobbyException("Terracotta 返回了与加入房间不匹配的状态：" + state.Kind + "。");
                await Task.Delay(PollInterval, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !active.Token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            string phase = lastState switch
            {
                StateKind.GuestConnecting => "核心仍处于 guest-connecting，尚未进入建立隧道阶段。请检查客户端是否能访问网络，以及房间码是否完整。",
                StateKind.GuestStarting => "核心已进入 guest-starting，但 Minecraft 转发尚未就绪。请房主确认世界仍然开放 LAN，双方退出并重新创建/加入房间。",
                _ => "客户端没有持续获取到 Terracotta 的有效加入状态。"
            };
            string detail = string.IsNullOrWhiteSpace(lastPollIssue) ? string.Empty : " 最近一次本地状态请求异常：" + lastPollIssue;
            throw new TimeoutException($"加入房间等待超时（{StartupTimeout.TotalSeconds:0} 秒）。最后状态：{lastState}。{phase} 房间码：{canonicalRoom}。请同时确认双方 Terracotta 版本兼容。{detail}");
        }
    }

    private async Task MonitorAsync(LobbyRuntime active)
    {
        string? lastSignature = null;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(active.Token).ConfigureAwait(false))
            {
                EnsureOwnedProcessRunning(active);
                TerracottaState state = await GetStateAsync(active.Port, active.Token).ConfigureAwait(false);
                if (state.Kind == StateKind.Exception)
                {
                    await StopUnexpectedlyAsync(active, MapException(state.ExceptionType)).ConfigureAwait(false);
                    return;
                }
                StateKind expected = active.IsHost ? StateKind.HostOk : StateKind.GuestOk;
                if (state.Kind != expected)
                {
                    await StopUnexpectedlyAsync(active, "Terracotta 联机状态已结束或服务异常。").ConfigureAwait(false);
                    return;
                }
                string signature = string.Join("\n", state.Players.Select(p => $"{p.MachineId}\u001f{p.DisplayName}\u001f{p.Kind}"));
                if (signature == lastSignature) continue;
                lastSignature = signature;
                TerracottaLobbySnapshot? snap = Current;
                if (snap is not null && ReferenceEquals(runtime, active)) Publish(snap with { Players = state.Players });
            }
        }
        catch (OperationCanceledException) when (active.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await StopUnexpectedlyAsync(active, "联机服务意外停止：" + ex.Message).ConfigureAwait(false);
        }
    }

    private async Task StopUnexpectedlyAsync(LobbyRuntime active, string message)
    {
        await operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(runtime, active)) return;
            runtime = null;
            await StopRuntimeAsync(active).ConfigureAwait(false);
            Volatile.Write(ref current, null);
        }
        finally { operationLock.Release(); }
        Stopped?.Invoke(message);
    }

    private async Task CleanupFailedAsync(LobbyRuntime? active)
    {
        if (active is null) return;
        if (ReferenceEquals(runtime, active)) runtime = null;
        Volatile.Write(ref current, null);
        await StopRuntimeAsync(active).ConfigureAwait(false);
    }

    private async Task StopRuntimeAsync(LobbyRuntime active)
    {
        active.RequestStop();
        if (active.ControllerStateStarted)
        {
            try
            {
                await SendCommandAsync(active.Port, "/state/ide", CancellationToken.None).ConfigureAwait(false);
                await WaitForWaitingAsync(active.Port).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException or TimeoutException or TaskCanceledException) { }
        }
        if (active.OwnedProcess is not null)
        {
            try
            {
                if (!active.OwnedProcess.HasExited)
                    await SendCommandAsync(active.Port, "/panic?peaceful=true", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException) { }
            await StopOwnedProcessAsync(active.OwnedProcess).ConfigureAwait(false);
        }
        active.Dispose();
    }

    private async Task WaitForWaitingAsync(int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            TerracottaState state = await GetStateAsync(port, timeout.Token).ConfigureAwait(false);
            if (state.Kind == StateKind.Waiting) return;
            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }
    }

    private async Task<TerracottaState> GetStateAsync(int port, CancellationToken ct)
    {
        using JsonDocument doc = await GetJsonAsync(port, "/state", ct).ConfigureAwait(false);
        return ParseState(doc.RootElement);
    }

    private async Task<JsonDocument> GetJsonAsync(int port, string path, CancellationToken ct)
    {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        requestTimeout.CancelAfter(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LoopbackUri(port, path));
            using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 1024 * 1024) throw new InvalidDataException("Terracotta 返回内容过大。");
            byte[] body = await response.Content.ReadAsByteArrayAsync(requestTimeout.Token).ConfigureAwait(false);
            if (body.Length > 1024 * 1024) throw new InvalidDataException("Terracotta 返回内容过大。");
            return JsonDocument.Parse(body);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Convert only this individual HTTP request's timeout. Do not let the poll loop
            // misreport it as the complete 90-second room-join timeout.
            throw new TimeoutException($"访问本机 Terracotta 状态接口超时：127.0.0.1:{port}{path}", ex);
        }
    }

    private async Task SendCommandAsync(int port, string path, CancellationToken ct, bool invalidRoomIsUserError = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, LoopbackUri(port, path));
        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (invalidRoomIsUserError && response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            throw new TerracottaLobbyException("Terracotta 拒绝了这个房间码，请检查是否输入正确。");
        response.EnsureSuccessStatusCode();
    }

    private static TerracottaState ParseState(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("state", out var stateEl) || stateEl.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Terracotta 状态响应格式无效。");
        StateKind kind = stateEl.GetString() switch
        {
            "waiting" => StateKind.Waiting,
            "host-scanning" => StateKind.HostScanning,
            "host-starting" => StateKind.HostStarting,
            "host-ok" => StateKind.HostOk,
            "guest-connecting" => StateKind.GuestConnecting,
            "guest-starting" => StateKind.GuestStarting,
            "guest-ok" => StateKind.GuestOk,
            "exception" => StateKind.Exception,
            _ => StateKind.Other
        };
        string room = root.TryGetProperty("room", out var roomEl) && roomEl.ValueKind == JsonValueKind.String
            ? CleanText(roomEl.GetString(), string.Empty, 64) : string.Empty;
        int? exceptionType = root.TryGetProperty("type", out var typeEl) && typeEl.TryGetInt32(out int t) ? t : null;
        var players = new List<TerracottaLobbyPlayer>();
        var machines = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement profile in profiles.EnumerateArray())
            {
                if (profile.ValueKind != JsonValueKind.Object) continue;
                string machineId = GetString(profile, "machine_id", 128);
                if (machineId.Length == 0 || !machines.Add(machineId)) continue;
                string name = CleanText(GetString(profile, "name", 64), "Player", 64);
                string vendor = CleanText(GetString(profile, "vendor", 128), "Terracotta", 128);
                string playerKind = GetString(profile, "kind", 16);
                bool isHost = playerKind.Equals("HOST", StringComparison.OrdinalIgnoreCase);
                bool isLocal = playerKind.Equals("LOCAL", StringComparison.OrdinalIgnoreCase) || (kind == StateKind.HostOk && isHost);
                players.Add(new TerracottaLobbyPlayer(name, machineId, vendor, isHost ? TerracottaPlayerKind.Host : TerracottaPlayerKind.Guest, isLocal));
            }
        }
        return new TerracottaState(kind, room, exceptionType, players);
    }

    private static string BuildHostPath(string name) => $"/state/scanning?player={Uri.EscapeDataString(CleanText(name, "Player", 64))}";
    private static string BuildGuestPath(string room, string name) => $"/state/guesting?room={Uri.EscapeDataString(CleanText(room, string.Empty, 256))}&player={Uri.EscapeDataString(CleanText(name, "Player", 64))}";
    private static Uri LoopbackUri(int port, string path) => new($"http://127.0.0.1:{port}{path}", UriKind.Absolute);

    private static int? TryReadExistingPort()
    {
        try
        {
            using var stream = new FileStream(LockFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> data = stackalloc byte[2];
            if (stream.Read(data) != 2) return null;
            int port = (data[0] << 8) | data[1];
            return port is > 0 and <= 65535 ? port : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void EnsureOwnedProcessRunning(LobbyRuntime active)
    {
        if (active.OwnedProcess?.HasExited == true) throw new InvalidOperationException("Terracotta 核心进程已退出。");
    }

    private void Publish(TerracottaLobbySnapshot snapshot)
    {
        Volatile.Write(ref current, snapshot);
        SnapshotChanged?.Invoke(snapshot);
    }

    private static string GetString(JsonElement root, string key, int maxLength)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) return string.Empty;
        return CleanText(value.GetString(), string.Empty, maxLength);
    }

    private static string CleanText(string? value, string fallback, int maximumLength)
    {
        string text = new((value ?? string.Empty).Trim().Where(c => !char.IsControl(c)).ToArray());
        if (text.Length == 0) text = fallback;
        return text.Length <= maximumLength ? text : text[..maximumLength];
    }

    private static string MapException(int? type) => type switch
    {
        0 => "无法连接到房主：未能 Ping 通房主。请检查房间码、房主是否在线及双方网络。",
        1 => "房主连接被重置。请让房主重新创建房间，并检查防火墙和安全软件。",
        2 => "Terracotta 客户端的 EasyTier 网络进程异常退出。请重新尝试加入。",
        3 => "房主的 EasyTier 网络进程异常退出。请房主重新创建房间。",
        4 => "Minecraft 转发连接被重置。请房主在游戏中重新“对局域网开放”，然后重新创建房间。",
        5 => "Terracotta 收到无效联机协议响应。请确认双方使用兼容版本，并都从启动器重新创建/加入。",
        _ => "Terracotta 报告联机错误（错误类型：" + (type?.ToString() ?? "未知") + "）。"
    };

    private static async Task DrainOutputAsync(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync().ConfigureAwait(false) is not null) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private static async Task StopOwnedProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (TimeoutException) { process.Kill(entireProcessTree: true); }
            }
            if (!process.HasExited) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException or ObjectDisposedException) { }
        finally { process.Dispose(); }
    }

    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(TerracottaMultiplayerClient));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
        operationLock.Dispose();
    }

    private sealed record Endpoint(int Port, Process? OwnedProcess);
    private sealed record TerracottaState(StateKind Kind, string RoomCode, int? ExceptionType, IReadOnlyList<TerracottaLobbyPlayer> Players);
    private enum StateKind { Waiting, HostScanning, HostStarting, HostOk, GuestConnecting, GuestStarting, GuestOk, Exception, Other }

    private sealed class LobbyRuntime : IDisposable
    {
        private readonly CancellationTokenSource shutdown = new();
        private int disposed;
        public LobbyRuntime(int port, Process? ownedProcess, bool isHost)
        {
            Port = port;
            OwnedProcess = ownedProcess;
            IsHost = isHost;
            Token = shutdown.Token;
        }
        public int Port { get; }
        public Process? OwnedProcess { get; }
        public bool IsHost { get; }
        public bool ControllerStateStarted { get; set; }
        public CancellationToken Token { get; }
        public Task? MonitorTask { get; set; }
        public void RequestStop() { if (!shutdown.IsCancellationRequested) shutdown.Cancel(); }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            RequestStop();
            shutdown.Dispose();
        }
    }
}
