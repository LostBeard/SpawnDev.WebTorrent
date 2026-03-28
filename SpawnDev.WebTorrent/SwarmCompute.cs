using SpawnDev.WebTorrent.Discovery;

namespace SpawnDev.WebTorrent;

/// <summary>
/// Swarm Compute — the foundation for AcceleratorType.P2P.
///
/// Coordinates distributed GPU compute across peer devices via the BitTorrent DHT.
/// Each peer contributes idle GPU cycles. Work is distributed transparently.
///
/// Architecture:
///   1. Host publishes a compute task (kernel + input data) as a torrent
///   2. Workers discover the task via AgentChannel subscription
///   3. Workers download input data via P2P piece exchange
///   4. Workers execute the kernel on their local GPU (any backend)
///   5. Workers publish results (gradient updates, output tensors) via BEP 46
///   6. Host aggregates results
///
/// Communication:
///   - Task distribution: torrent pieces over WebRTC data channels
///   - Coordination: AgentChannel named channels (DHT mutable items)
///   - Gradient sync: TurboQuant compressed (3-4 bit) via torrent
///   - Result collection: BEP 46 signed mutable items
///
/// Security:
///   - ECDSA-P256 signed task assignments (WebCrypto native)
///   - Workers verify task authenticity before execution
///   - Results are signed — host verifies worker identity
///   - Optional: encrypted data channels for sensitive models
///
/// This class is the design scaffold for SpawnDev.ILGPU AcceleratorType.P2P.
/// The actual kernel dispatch will be implemented in SpawnDev.ILGPU once
/// this transport layer is proven.
/// </summary>
public class SwarmCompute : IAsyncDisposable
{
    private readonly WebTorrentClient _torrentClient;
    private readonly AgentChannel _agentChannel;
    private readonly List<SwarmWorker> _workers = new();

    /// <summary>Our agent identity for task publishing.</summary>
    public byte[] PublicKey => _agentChannel.PublicKey;

    /// <summary>Number of connected workers.</summary>
    public int WorkerCount => _workers.Count;

    /// <summary>Fired when a worker joins the swarm.</summary>
    public event Action<SwarmWorker>? OnWorkerJoined;

    /// <summary>Fired when a worker submits results.</summary>
    public event Action<SwarmWorker, byte[]>? OnResultReceived;

    /// <summary>Fired when all workers complete a task.</summary>
    public event Action<SwarmTask>? OnTaskComplete;

    public SwarmCompute(WebTorrentClient torrentClient, DhtDiscovery dht)
    {
        _torrentClient = torrentClient;
        _agentChannel = new AgentChannel(dht);

        // Listen for worker announcements
        var workers = _agentChannel.Channel("workers");
        _agentChannel.OnAgentUpdate += (pubKey, value, seq) =>
        {
            // Worker announced itself — add to pool
            var worker = new SwarmWorker
            {
                PublicKey = pubKey,
                Capabilities = value,
                JoinedAt = DateTime.UtcNow,
            };
            _workers.Add(worker);
            OnWorkerJoined?.Invoke(worker);
        };
    }

    /// <summary>
    /// Publish a compute task to the swarm.
    /// Workers will discover it, download inputs, execute, and publish results.
    /// </summary>
    /// <param name="taskData">Serialized task description (kernel ID, parameters, input torrent hash).</param>
    /// <param name="inputData">Input data to distribute to workers.</param>
    /// <returns>The task handle for tracking progress.</returns>
    public async Task<SwarmTask> PublishTaskAsync(byte[] taskData, byte[]? inputData = null)
    {
        var task = new SwarmTask
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
        };

        // If input data provided, seed it as a torrent
        if (inputData != null)
        {
            var swarm = await _torrentClient.SeedAsync(inputData, $"task-{task.Id}.bin");
            task.InputInfoHash = swarm.InfoHash;
            task.InputMagnetUri = swarm.MagnetURI;
        }

        // Publish task description via AgentChannel
        var tasks = _agentChannel.Channel("tasks");
        await tasks.PublishAsync(taskData);

        return task;
    }

    /// <summary>
    /// Join the swarm as a worker. Announce capabilities and listen for tasks.
    /// </summary>
    /// <param name="capabilities">Device capabilities (GPU type, VRAM, backend).</param>
    /// <param name="onTask">Callback when a task is received.</param>
    public async Task JoinAsWorkerAsync(byte[] capabilities, Func<byte[], Task<byte[]>>? onTask = null)
    {
        // Announce ourselves
        var workers = _agentChannel.Channel("workers");
        await workers.PublishAsync(capabilities);

        // Listen for tasks
        // (Would subscribe to the host's public key for task channel)
    }

    /// <summary>
    /// Submit results for a task.
    /// </summary>
    public async Task SubmitResultAsync(SwarmTask task, byte[] results)
    {
        var resultChannel = _agentChannel.Channel($"results-{task.Id}");
        await resultChannel.PublishAsync(results);
    }

    public async ValueTask DisposeAsync()
    {
        await _agentChannel.DisposeAsync();
    }
}

/// <summary>A worker in the compute swarm.</summary>
public class SwarmWorker
{
    /// <summary>Worker's public key identity.</summary>
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();

    /// <summary>Worker capabilities (GPU type, VRAM, backend).</summary>
    public byte[] Capabilities { get; set; } = Array.Empty<byte>();

    /// <summary>When the worker joined.</summary>
    public DateTime JoinedAt { get; set; }
}

/// <summary>A distributed compute task.</summary>
public class SwarmTask
{
    /// <summary>Unique task identifier.</summary>
    public string Id { get; set; } = "";

    /// <summary>When the task was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Info hash of the input data torrent.</summary>
    public byte[]? InputInfoHash { get; set; }

    /// <summary>Magnet URI for workers to download input data.</summary>
    public string? InputMagnetUri { get; set; }

    /// <summary>Number of workers that have submitted results.</summary>
    public int CompletedWorkers { get; set; }

    /// <summary>Whether all expected results have been received.</summary>
    public bool IsComplete { get; set; }
}
