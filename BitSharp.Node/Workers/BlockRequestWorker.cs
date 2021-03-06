﻿using BitSharp.Common;
using BitSharp.Common.ExtensionMethods;
using BitSharp.Core;
using BitSharp.Core.Domain;
using BitSharp.Core.Storage;
using BitSharp.Node.Domain;
using BitSharp.Node.Network;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BitSharp.Node.Workers
{
    public class BlockRequestWorker : Worker
    {
        private static readonly TimeSpan STALE_REQUEST_TIME = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan MISSING_STALE_REQUEST_TIME = TimeSpan.FromSeconds(15);

        private readonly Logger logger;
        private readonly LocalClient localClient;
        private readonly CoreDaemon blockchainDaemon;
        private readonly ChainedHeaderCache chainedHeaderCache;
        private readonly BlockCache blockCache;

        private readonly ConcurrentDictionary<UInt256, DateTime> allBlockRequests;
        private readonly ConcurrentDictionary<IPEndPoint, ConcurrentDictionary<UInt256, DateTime>> blockRequestsByPeer;

        private SortedList<int, ChainedHeader> missingBlockQueue;

        private List<ChainedHeader> targetChainQueue;
        private int targetChainQueueIndex;
        private DateTime targetChainQueueTime;

        private readonly DurationMeasure blockRequestDurationMeasure;
        private readonly RateMeasure blockDownloadRateMeasure;
        private readonly RateMeasure duplicateBlockDownloadRateMeasure;

        private int targetChainLookAhead;
        private int criticalTargetChainLookAhead;

        private readonly WorkerMethod flushWorker;
        private readonly ConcurrentQueue<Tuple<RemoteNode, Block>> flushQueue;

        public BlockRequestWorker(Logger logger, WorkerConfig workerConfig, LocalClient localClient, CoreDaemon blockchainDaemon, ChainedHeaderCache chainedHeaderCache, BlockCache blockCache)
            : base("BlockRequestWorker", workerConfig.initialNotify, workerConfig.minIdleTime, workerConfig.maxIdleTime, logger)
        {
            this.logger = logger;
            this.localClient = localClient;
            this.blockchainDaemon = blockchainDaemon;
            this.chainedHeaderCache = chainedHeaderCache;
            this.blockCache = blockCache;

            this.allBlockRequests = new ConcurrentDictionary<UInt256, DateTime>();
            this.blockRequestsByPeer = new ConcurrentDictionary<IPEndPoint, ConcurrentDictionary<UInt256, DateTime>>();
            this.missingBlockQueue = new SortedList<int, ChainedHeader>();

            this.localClient.OnBlock += HandleBlock;
            this.blockchainDaemon.OnChainStateChanged += HandleChainStateChanged;
            this.blockchainDaemon.OnTargetChainChanged += HandleTargetChainChanged;
            this.blockCache.OnMissing += HandleBlockMissing;

            this.blockRequestDurationMeasure = new DurationMeasure(sampleCutoff: TimeSpan.FromMinutes(5));
            this.blockDownloadRateMeasure = new RateMeasure();
            this.duplicateBlockDownloadRateMeasure = new RateMeasure();

            this.targetChainLookAhead = 1;
            this.criticalTargetChainLookAhead = 1;

            this.flushWorker = new WorkerMethod("BlockRequestWorker.FlushWorker", FlushWorkerMethod, initialNotify: true, minIdleTime: TimeSpan.Zero, maxIdleTime: TimeSpan.MaxValue, logger: this.logger);
            this.flushQueue = new ConcurrentQueue<Tuple<RemoteNode, Block>>();
        }

        public float GetBlockDownloadRate(TimeSpan perUnitTime)
        {
            return this.blockDownloadRateMeasure.GetAverage(perUnitTime);
        }

        public float GetDuplicateBlockDownloadRate(TimeSpan perUnitTime)
        {
            return this.duplicateBlockDownloadRateMeasure.GetAverage(perUnitTime);
        }

        protected override void SubDispose()
        {
            this.localClient.OnBlock -= HandleBlock;
            this.blockchainDaemon.OnChainStateChanged -= HandleChainStateChanged;
            this.blockchainDaemon.OnTargetChainChanged -= HandleTargetChainChanged;
            this.blockCache.OnMissing -= HandleBlockMissing;

            this.blockRequestDurationMeasure.Dispose();
            this.blockDownloadRateMeasure.Dispose();
            this.duplicateBlockDownloadRateMeasure.Dispose();

            this.flushWorker.Dispose();
        }

        protected override void SubStart()
        {
            this.flushWorker.Start();
        }

        protected override void SubStop()
        {
            this.flushWorker.Stop();
        }

        protected override void WorkAction()
        {
            // update rates
            new MethodTimer(false).Time("UpdateLookAhead", () =>
                UpdateLookAhead());

            // update list of missing blocks to request
            new MethodTimer(false).Time("UpdateMissingBlockQueue", () =>
                UpdateMissingBlockQueue());

            // update list of blocks on target chain to request
            new MethodTimer(false).Time("UpdateTargetChainQueue", () =>
                UpdateTargetChainQueue());

            // send out request to peers
            //      missing blocks will be requested from every peer
            //      target chain blocks will be requested from each peer in non-overlapping chunks
            new MethodTimer(false).Time("SendBlockRequests", () =>
                SendBlockRequests());
        }

        private void UpdateLookAhead()
        {
            //TODO this needs to work properly when the internet connection is slower than blocks can be processed
            
            var blockProcessingTime = this.blockchainDaemon.AverageBlockProcessingTime();
            if (blockProcessingTime == TimeSpan.Zero)
            {
                this.targetChainLookAhead = 1;
                this.criticalTargetChainLookAhead = 1;
            }
            else
            {
                // get average block request time
                var avgBlockRequestTime = this.blockRequestDurationMeasure.GetAverage();

                // determine target chain look ahead
                var lookAheadTime = avgBlockRequestTime + TimeSpan.FromSeconds(30);
                this.targetChainLookAhead = 1 + (int)(lookAheadTime.TotalSeconds / blockProcessingTime.TotalSeconds);

                // determine critical target chain look ahead
                var criticalLookAheadTime = TimeSpan.FromSeconds(5);
                this.criticalTargetChainLookAhead = 1 + (int)(criticalLookAheadTime.TotalSeconds / blockProcessingTime.TotalSeconds);

                this.logger.Debug(new string('-', 80));
                this.logger.Debug("Block Request Time: {0}".Format2(avgBlockRequestTime));
                this.logger.Debug("Look Ahead: {0:#,##0}".Format2(this.targetChainLookAhead));
                this.logger.Debug("Critical Look Ahead: {0:#,##0}".Format2(this.criticalTargetChainLookAhead));
                this.logger.Debug("Missing Block Queue Count: {0:#,##0}".Format2(this.missingBlockQueue.Count));
                this.logger.Debug("Block Request Count: {0:#,##0}".Format2(this.allBlockRequests.Count));
                this.logger.Debug(new string('-', 80));
            }
        }

        private void UpdateMissingBlockQueue()
        {
            var currentChainLocal = this.blockchainDaemon.CurrentChain;
            var targetChainLocal = this.blockchainDaemon.TargetChain;

            // remove any blocks that are no longer missing
            this.missingBlockQueue.RemoveWhere(x => this.blockCache.ContainsKey(x.Value.Hash));

            // remove old missing blocks
            this.missingBlockQueue.RemoveWhere(x => x.Value.Height < currentChainLocal.Height);

            // add any blocks that are currently missing
            foreach (var missingBlock in this.blockCache.MissingData)
            {
                ChainedHeader missingBlockChained;
                if (this.chainedHeaderCache.TryGetValue(missingBlock, out missingBlockChained))
                {
                    this.missingBlockQueue[missingBlockChained.Height] = missingBlockChained;
                }
            }

            // preemptively add any upcoming blocks on the target chain that are missing
            if (targetChainLocal != null)
            {
                foreach (var upcomingBlock in
                    currentChainLocal.NavigateTowards(targetChainLocal)
                    .Select(x => x.Item2)
                    .Take(this.criticalTargetChainLookAhead)
                    .Where(x =>
                        !this.missingBlockQueue.ContainsKey(x.Height)
                        && !this.blockCache.ContainsKey(x.Hash)))
                {
                    this.missingBlockQueue[upcomingBlock.Height] = upcomingBlock;
                }
            }
        }

        private void UpdateTargetChainQueue()
        {
            var currentChainLocal = this.blockchainDaemon.CurrentChain;
            var targetChainLocal = this.blockchainDaemon.TargetChain;

            // update the target chain queue at most once per second
            if (this.targetChainQueueTime != null && DateTime.UtcNow - targetChainQueueTime < TimeSpan.FromSeconds(1))
                return;
            else
                this.targetChainQueueTime = DateTime.UtcNow;

            // find missing blocks on the target chain to be requested, taking a chunk at a time
            if (targetChainLocal != null &&
                (this.targetChainQueue == null || this.targetChainQueueIndex >= this.targetChainQueue.Count))
            {
                this.targetChainQueue = currentChainLocal.NavigateTowards(targetChainLocal)
                    .Select(x => x.Item2)
                    .Take(this.targetChainLookAhead)
                    .Where(x => !this.blockCache.ContainsKey(x.Hash))
                    .ToList();
                this.targetChainQueueIndex = 0;
            }
        }

        private void SendBlockRequests()
        {
            var now = DateTime.UtcNow;
            var requestTasks = new List<Task>();

            // remove any stale requests from the global list of requests
            this.allBlockRequests.RemoveWhere(x => (now - x.Value) > STALE_REQUEST_TIME);

            // remove any stale requests for missing blocks, using a shorter timeout
            var missingBlockHashes = new HashSet<UInt256>(this.missingBlockQueue.Values.Select(x => x.Hash));
            this.allBlockRequests.RemoveWhere(x => missingBlockHashes.Contains(x.Key) && (now - x.Value) > MISSING_STALE_REQUEST_TIME);

            var peerCount = this.localClient.ConnectedPeers.Count;
            if (peerCount == 0)
                return;

            var requestsPerPeer = Math.Max(1, this.missingBlockQueue.Count + (this.targetChainLookAhead / peerCount * 5));

            // loop through each connected peer
            foreach (var peer in this.localClient.ConnectedPeers)
            {
                // retrieve the peer's currently requested blocks
                var peerBlockRequests = this.blockRequestsByPeer.AddOrUpdate(
                    peer.Key,
                    addKey => new ConcurrentDictionary<UInt256, DateTime>(),
                    (existingKey, existingValue) => existingValue);

                // remove any stale requests from the peer's list of requests
                peerBlockRequests.RemoveWhere(x => (now - x.Value) > STALE_REQUEST_TIME);

                // determine the number of requests that can be sent to the peer
                var requestCount = requestsPerPeer - peerBlockRequests.Count;
                if (requestCount > 0)
                {
                    // iterate through the blocks that should be requested for this peer
                    var invVectors = new List<InventoryVector>();
                    foreach (var requestBlock in GetRequestBlocksForPeer(requestCount, peerBlockRequests))
                    {
                        // track block requests
                        peerBlockRequests[requestBlock] = now;
                        this.allBlockRequests.TryAdd(requestBlock, now);

                        // add block to inv request
                        invVectors.Add(new InventoryVector(InventoryVector.TYPE_MESSAGE_BLOCK, requestBlock));
                    }

                    // send out the request for blocks
                    requestTasks.Add(peer.Value.Sender.SendGetData(invVectors.ToImmutableArray()));
                }
            }

            // notify for another loop of work when out of target chain queue to use, unless there is nothing left missing
            if (this.targetChainQueue != null && this.targetChainQueueIndex >= this.targetChainQueue.Count && this.missingBlockQueue.Count > 0)
                this.ForceWork();
        }

        private IEnumerable<UInt256> GetRequestBlocksForPeer(int count, ConcurrentDictionary<UInt256, DateTime> peerBlockRequests)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException("count");
            else if (count == 0)
                yield break;

            // keep track of blocks iterated blocks for peer
            var currentCount = 0;

            // iterate through any missing blocks first, they have priority and requests go stale more quickly
            foreach (var missingBlock in this.missingBlockQueue.Values)
            {
                if (currentCount >= count)
                    break;

                if (!peerBlockRequests.ContainsKey(missingBlock.Hash)
                    && !this.allBlockRequests.ContainsKey(missingBlock.Hash)
                    && !this.blockCache.ContainsKey(missingBlock.Hash))
                {
                    yield return missingBlock.Hash;
                    currentCount++;
                }
            }

            // iterate through the blocks on the target chain, each peer will request a separate chunk of blocks
            for (; this.targetChainQueue != null && this.targetChainQueueIndex < this.targetChainQueue.Count && currentCount < count; this.targetChainQueueIndex++)
            {
                var requestBlock = this.targetChainQueue[this.targetChainQueueIndex].Hash;

                if (!peerBlockRequests.ContainsKey(requestBlock)
                    && !this.allBlockRequests.ContainsKey(requestBlock)
                    && !this.blockCache.ContainsKey(requestBlock))
                {
                    yield return requestBlock;
                    currentCount++;
                }
            }
        }

        private void FlushWorkerMethod()
        {
            Tuple<RemoteNode, Block> tuple;
            while (this.flushQueue.TryDequeue(out tuple))
            {
                // cooperative loop
                this.ThrowIfCancelled();

                var remoteNode = tuple.Item1;
                var block = tuple.Item2;

                if (this.blockCache.TryAdd(block.Hash, block))
                    this.blockDownloadRateMeasure.Tick();
                else
                    this.duplicateBlockDownloadRateMeasure.Tick();

                DateTime requestTime;
                if (this.allBlockRequests.TryRemove(block.Hash, out requestTime))
                {
                    this.blockRequestDurationMeasure.Tick(DateTime.UtcNow - requestTime);
                }

                ConcurrentDictionary<UInt256, DateTime> peerBlockRequests;
                if (this.blockRequestsByPeer.TryGetValue(remoteNode.RemoteEndPoint, out peerBlockRequests))
                {
                    peerBlockRequests.TryRemove(block.Hash, out requestTime);
                }

                this.NotifyWork();
            }
        }

        private void HandleBlock(RemoteNode remoteNode, Block block)
        {
            this.flushQueue.Enqueue(Tuple.Create(remoteNode, block));
            this.flushWorker.NotifyWork();
        }

        private void HandleChainStateChanged(object sender, EventArgs e)
        {
            this.NotifyWork();
        }

        private void HandleTargetChainChanged(object sender, EventArgs e)
        {
            this.NotifyWork();
        }

        private void HandleBlockMissing(UInt256 blockHash)
        {
            this.NotifyWork();
        }

        private sealed class HeightComparer : IComparer<ChainedHeader>
        {
            public int Compare(ChainedHeader x, ChainedHeader y)
            {
                return x.Height - y.Height;
            }
        }
    }
}
