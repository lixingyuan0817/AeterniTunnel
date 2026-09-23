using System.Text.Json;
using DataChannelDotnet;
using DataChannelDotnet.Bindings;
using DataChannelDotnet.Data;
using DataChannelDotnet.Events;
using DataChannelDotnet.Impl;

if (args.Length < 3 || !string.Equals(args[0], "--role", StringComparison.OrdinalIgnoreCase) ||
    !string.Equals(args[1], "offer", StringComparison.OrdinalIgnoreCase) &&
    !string.Equals(args[1], "answer", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("usage: dotnet run -- --role offer|answer <signal-directory>");
    return 2;
}

var role = args[1].ToLowerInvariant();
var directory = Path.GetFullPath(args[2]);
Directory.CreateDirectory(directory);
var prefix = Path.Combine(directory, role);
var peer = new RtcPeerConnection(new RtcPeerConfiguration
{
    BindAddress = "127.0.0.1",
    PortRangeBegin = 40000,
    PortRangeEnd = 40100,
    MaxMessageSize = 262_144,
    IceServers = [],
});

using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var reliableReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
var realtimeReceived = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
var reliable = new TaskCompletionSource<IRtcDataChannel>(TaskCreationOptions.RunContinuationsAsynchronously);
var realtime = new TaskCompletionSource<IRtcDataChannel>(TaskCreationOptions.RunContinuationsAsynchronously);
var realtimeCount = 0;

peer.OnConnectionStateChange += (_, state) =>
{
    Console.WriteLine($"state={state}");
    if (state == rtcState.RTC_CONNECTED)
        connected.TrySetResult();
    else if (state is rtcState.RTC_FAILED or rtcState.RTC_CLOSED)
        connected.TrySetException(new InvalidOperationException($"peer connection state {state}"));
};
peer.OnIceStateChange += (_, state) => Console.WriteLine($"ice={state}");
peer.OnCandidateSafe += (_, candidate) => AppendJson(prefix + ".candidates", candidate);
peer.OnLocalDescriptionSafe += (_, description) =>
{
    File.WriteAllText(prefix + ".description", JsonSerializer.Serialize(description));
    Console.WriteLine($"description={description.Type} bytes={description.Sdp.Length}");
};
peer.OnDataChannel += (_, channel) => AttachChannel(channel);
Task? exchangeTask = null;

void AttachChannel(IRtcDataChannel channel)
{
    Console.WriteLine($"channel={channel.Label}");
    if (channel.Label == "reliable")
    {
        reliable.TrySetResult(channel);
        channel.OnTextReceivedSafe += (_, message) =>
        {
            reliableReceived.TrySetResult(message.Text);
            if (role == "answer")
                channel.Send("ack:" + message.Text);
        };
    }
    else if (channel.Label == "realtime")
    {
        realtime.TrySetResult(channel);
        channel.OnBinaryReceivedSafe += (_, _) =>
        {
            var count = Interlocked.Increment(ref realtimeCount);
            if (count >= 10)
                realtimeReceived.TrySetResult(count);
        };
    }
}

if (role == "offer")
{
    var reliableChannel = peer.CreateDataChannel(new RtcCreateDataChannelArgs { Label = "reliable" });
    var realtimeChannel = peer.CreateDataChannel(new RtcCreateDataChannelArgs
    {
        Label = "realtime",
        Unordered = true,
        Unreliable = true,
        MaxRetransmits = 0,
    });
    AttachChannel(reliableChannel);
    AttachChannel(realtimeChannel);
    reliableChannel.OnOpen += _ => reliable.TrySetResult(reliableChannel);
    realtimeChannel.OnOpen += _ => realtime.TrySetResult(realtimeChannel);
}
else
{
    await WaitForFileAsync(directory, "offer.description", exit.Token);
    var offer = JsonSerializer.Deserialize<RtcDescription>(File.ReadAllText(Path.Combine(directory, "offer.description")))
        ?? throw new InvalidDataException("offer description is empty");
    peer.SetRemoteDescription(offer);
    exchangeTask = ExchangeCandidatesAsync(peer, directory, role, exit.Token);
}

await WaitForFileAsync(directory, role == "offer" ? "answer.description" : "offer.description", exit.Token);
if (role == "offer")
{
    var answer = JsonSerializer.Deserialize<RtcDescription>(File.ReadAllText(Path.Combine(directory, "answer.description")))
        ?? throw new InvalidDataException("answer description is empty");
    peer.SetRemoteDescription(answer);
    exchangeTask = ExchangeCandidatesAsync(peer, directory, role, exit.Token);
}

await connected.Task.WaitAsync(exit.Token);
var selected = peer.TryGetSelectedCandidatePair(out var pair) ? $"local={pair.LocalCandidate};remote={pair.RemoteCandidate}" : "selected_pair=unavailable";
Console.WriteLine(selected);

if (role == "offer")
{
    var reliableChannel = await reliable.Task.WaitAsync(exit.Token);
    var realtimeChannel = await realtime.Task.WaitAsync(exit.Token);
    var payload = "synthetic-peer-message:" + Guid.NewGuid().ToString("N");
    reliableChannel.Send(payload);
    for (var i = 0; i < 10; i++)
        realtimeChannel.Send(new byte[] { 0x50, (byte)i, 0x2F });
    var ack = await reliableReceived.Task.WaitAsync(exit.Token);
    Console.WriteLine($"reliable_ack={ack}");
    Console.WriteLine("realtime_sent=10");
}
else
{
    var message = await reliableReceived.Task.WaitAsync(exit.Token);
    Console.WriteLine($"reliable_message={message}");
    Console.WriteLine($"realtime_received={(await realtimeReceived.Task.WaitAsync(exit.Token))}");
}

peer.Dispose();
exit.Cancel();
if (exchangeTask is not null)
{
    try { await exchangeTask; } catch (OperationCanceledException) { }
}
Console.WriteLine("closed=true");
return 0;

static async Task WaitForFileAsync(string directory, string name, CancellationToken ct)
{
    var path = Path.Combine(directory, name);
    while (!File.Exists(path))
        await Task.Delay(25, ct);
}

static void AppendJson(string path, RtcCandidate candidate)
{
    var line = JsonSerializer.Serialize(candidate) + Environment.NewLine;
    File.AppendAllText(path, line);
}

static async Task ExchangeCandidatesAsync(IRtcPeerConnection peer, string directory, string role, CancellationToken ct)
{
    var remotePath = Path.Combine(directory, role == "offer" ? "answer.candidates" : "offer.candidates");
    var lineOffset = 0;
    while (!ct.IsCancellationRequested)
    {
        if (File.Exists(remotePath))
        {
            var lines = await File.ReadAllLinesAsync(remotePath, ct);
            for (var i = lineOffset; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var candidate = JsonSerializer.Deserialize<RtcCandidate>(line);
                    if (candidate is not null)
                        peer.AddRemoteCandidate(candidate);
                }
            }
            lineOffset = lines.Length;
        }
        await Task.Delay(25, ct);
    }
}
