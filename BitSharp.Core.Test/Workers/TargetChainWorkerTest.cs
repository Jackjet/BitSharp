﻿using BitSharp.Blockchain;
using BitSharp.Common;
using BitSharp.Common.ExtensionMethods;
using BitSharp.Core.Domain;
using BitSharp.Core.Rules;
using BitSharp.Core.Storage;
using BitSharp.Core.Storage.Memory;
using BitSharp.Core.Workers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Ninject;
using Ninject.Parameters;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BitSharp.Core.Test.Workers
{
    [TestClass]
    public class TargetChainWorkerTest
    {
        //TODO
        // where i have:
        //      wait for worker (chained block addition)
        //      wait for worker (target block changed)
        // these two notifications could potentially complete in one loop, and then the second wait will hang forever

        [TestMethod]
        public void TestSimpleChain()
        {
            // prepare test kernel
            var kernel = new StandardKernel(new ConsoleLoggingModule(), new MemoryStorageModule(), new CoreCacheModule());

            // initialize data
            var fakeHeaders = new FakeHeaders();
            var chainedHeader0 = new ChainedHeader(fakeHeaders.Genesis(), height: 0, totalWork: 0);
            var chainedHeader1 = new ChainedHeader(fakeHeaders.Next(), height: 1, totalWork: 1);
            var chainedHeader2 = new ChainedHeader(fakeHeaders.Next(), height: 2, totalWork: 2);

            // mock rules
            var mockRules = new Mock<IBlockchainRules>();
            mockRules.Setup(rules => rules.GenesisChainedHeader).Returns(chainedHeader0);
            kernel.Bind<IBlockchainRules>().ToConstant(mockRules.Object);

            // store genesis block
            var chainedHeaderCache = kernel.Get<ChainedHeaderCache>();
            chainedHeaderCache[chainedHeader0.Hash] = chainedHeader0;

            // initialize the target chain worker
            using (var targetChainWorker = kernel.Get<TargetChainWorker>(new ConstructorArgument("workerConfig", new WorkerConfig(initialNotify: false, minIdleTime: TimeSpan.Zero, maxIdleTime: TimeSpan.MaxValue))))
            {
                // verify initial state
                Assert.AreEqual(null, targetChainWorker.TargetBlock);
                Assert.AreEqual(null, targetChainWorker.TargetChain);

                // monitor event firing
                var workNotifyEvent = new AutoResetEvent(false);
                var workStoppedEvent = new AutoResetEvent(false);
                var onTargetChainChangedCount = 0;

                targetChainWorker.OnNotifyWork += () => workNotifyEvent.Set();
                targetChainWorker.OnWorkStopped += () => workStoppedEvent.Set();
                targetChainWorker.OnTargetChainChanged += () => onTargetChainChangedCount++;

                // start worker and wait for initial chain
                targetChainWorker.Start();
                workNotifyEvent.WaitOne();
                workStoppedEvent.WaitOne();

                // verify chained to block 0
                Assert.AreEqual(chainedHeader0, targetChainWorker.TargetBlock);
                AssertBlockListEquals(new[] { chainedHeader0 }, targetChainWorker.TargetChain.Blocks);
                Assert.AreEqual(1, onTargetChainChangedCount);

                // add block 1
                chainedHeaderCache[chainedHeader1.Hash] = chainedHeader1;

                // wait for worker (chained block addition)
                workNotifyEvent.WaitOne();
                workStoppedEvent.WaitOne();
                // wait for worker (target block changed)
                workNotifyEvent.WaitOneOrFail(1000);
                workStoppedEvent.WaitOneOrFail(1000);

                // verify chained to block 1
                Assert.AreEqual(chainedHeader1, targetChainWorker.TargetBlock);
                AssertBlockListEquals(new[] { chainedHeader0, chainedHeader1 }, targetChainWorker.TargetChain.Blocks);
                Assert.AreEqual(2, onTargetChainChangedCount);

                // add block 2
                chainedHeaderCache[chainedHeader2.Hash] = chainedHeader2;

                // wait for worker (chained block addition)
                workNotifyEvent.WaitOne();
                workStoppedEvent.WaitOne();
                // wait for worker (target block changed)
                workNotifyEvent.WaitOneOrFail(1000);
                workStoppedEvent.WaitOneOrFail(1000);

                // verify chained to block 2
                Assert.AreEqual(chainedHeader2, targetChainWorker.TargetBlock);
                AssertBlockListEquals(new[] { chainedHeader0, chainedHeader1, chainedHeader2 }, targetChainWorker.TargetChain.Blocks);
                Assert.AreEqual(3, onTargetChainChangedCount);

                // verify no other work was done
                Assert.IsFalse(workNotifyEvent.WaitOne(0));
                Assert.IsFalse(workStoppedEvent.WaitOne(0));
            }
        }

        [TestMethod]
        public void TestSimpleChainReverse()
        {
            // prepare test kernel
            var kernel = new StandardKernel(new ConsoleLoggingModule(), new MemoryStorageModule(), new CoreCacheModule());

            // initialize data
            var fakeHeaders = new FakeHeaders();
            var chainedHeader0 = new ChainedHeader(fakeHeaders.Genesis(), height: 0, totalWork: 0);
            var chainedHeader1 = new ChainedHeader(fakeHeaders.Next(), height: 1, totalWork: 1);
            var chainedHeader2 = new ChainedHeader(fakeHeaders.Next(), height: 2, totalWork: 2);
            var chainedHeader3 = new ChainedHeader(fakeHeaders.Next(), height: 3, totalWork: 3);
            var chainedHeader4 = new ChainedHeader(fakeHeaders.Next(), height: 4, totalWork: 4);

            // mock rules
            var mockRules = new Mock<IBlockchainRules>();
            mockRules.Setup(rules => rules.GenesisChainedHeader).Returns(chainedHeader0);
            kernel.Bind<IBlockchainRules>().ToConstant(mockRules.Object);

            // store genesis block
            var chainedHeaderCache = kernel.Get<ChainedHeaderCache>();
            chainedHeaderCache[chainedHeader0.Hash] = chainedHeader0;

            // initialize the target chain worker
            using (var targetChainWorker = kernel.Get<TargetChainWorker>(new ConstructorArgument("workerConfig", new WorkerConfig(initialNotify: false, minIdleTime: TimeSpan.Zero, maxIdleTime: TimeSpan.MaxValue))))
            {
                // verify initial state
                Assert.AreEqual(null, targetChainWorker.TargetBlock);
                Assert.AreEqual(null, targetChainWorker.TargetChain);

                // monitor event firing
                var workNotifyEvent = new AutoResetEvent(false);
                var workStoppedEvent = new AutoResetEvent(false);
                var onTargetChainChangedCount = 0;

                targetChainWorker.OnNotifyWork += () => workNotifyEvent.Set();
                targetChainWorker.OnWorkStopped += () => workStoppedEvent.Set();
                targetChainWorker.OnTargetChainChanged += () => onTargetChainChangedCount++;

                // start worker and wait for initial chain
                targetChainWorker.Start();
                workNotifyEvent.WaitOne();
                workStoppedEvent.WaitOne();

                // verify chained to block 0
                Assert.AreEqual(chainedHeader0, targetChainWorker.TargetBlock);
                AssertBlockListEquals(new[] { chainedHeader0 }, targetChainWorker.TargetChain.Blocks);
                Assert.AreEqual(1, onTargetChainChangedCount);

                // add block 4
                chainedHeaderCache[chainedHeader4.Hash] = chainedHeader4;

                // wait for worker (chained block addition)
                workNotifyEvent.WaitOne();
                workStoppedEvent.WaitOne();
                // wait for worker (target block changed)
                workNotifyEvent.WaitOneOrFail(1000);
                workStoppedEvent.WaitOneOrFail(1000);

                // verify no work done, but the target block should still be updated
                Assert.AreEqual(chainedHeader4, targetChainWorker.TargetBlock);
                AssertBlockListEquals(new[] { chainedHeader0 }, targetChainWorker.TargetChain.Blocks);
                Assert.AreEqual(1, onTargetChainChangedCount);

                // add block 3
                chainedHeaderCache[chainedHeader3.Hash] = chainedHeader3;

                // wait for worker (chained block addition)
                workNotifyEvent.WaitOne();
                workStoppedEvent.WaitOne();

                // verify no work done
                Assert.AreEqual(chainedHeader4, targetChainWorker.TargetBlock);
                AssertBlockListEquals(new[] { chainedHeader0 }, targetChainWorker.TargetChain.Blocks);
                Assert.AreEqual(1, onTargetChainChangedCount);

                // add block 2
                chainedHeaderCache[chainedHeader2.Hash] = chainedHeader2;

                // wait for worker (chained block addition)
                workNotifyEvent.WaitOne();
                workStoppedEvent.WaitOne();

                // verify no work done
                Assert.AreEqual(chainedHeader4, targetChainWorker.TargetBlock);
                AssertBlockListEquals(new[] { chainedHeader0 }, targetChainWorker.TargetChain.Blocks);
                Assert.AreEqual(1, onTargetChainChangedCount);

                // add block 1
                chainedHeaderCache[chainedHeader1.Hash] = chainedHeader1;

                // wait for worker (chained block addition)
                workNotifyEvent.WaitOne();
                workStoppedEvent.WaitOne();

                // verify chained to block 4
                Assert.AreEqual(chainedHeader4, targetChainWorker.TargetBlock);
                AssertBlockListEquals(new[] { chainedHeader0, chainedHeader1, chainedHeader2, chainedHeader3, chainedHeader4 }, targetChainWorker.TargetChain.Blocks);
                Assert.AreEqual(2, onTargetChainChangedCount);

                // verify no other work was done
                Assert.IsFalse(workNotifyEvent.WaitOne(0));
                Assert.IsFalse(workStoppedEvent.WaitOne(0));
            }
        }

        [TestMethod]
        public void TestTargetChainReorganize()
        {
            // prepare test kernel
            var kernel = new StandardKernel(new ConsoleLoggingModule(), new MemoryStorageModule(), new CoreCacheModule());

            // initialize data
            var fakeHeaders = new FakeHeaders();
            var chainedHeader0 = new ChainedHeader(fakeHeaders.Genesis(), height: 0, totalWork: 0);
            var chainedHeader1 = new ChainedHeader(fakeHeaders.Next(), height: 1, totalWork: 1);
            var chainedHeader2 = new ChainedHeader(fakeHeaders.Next(), height: 2, totalWork: 2);

            var fakeHeadersA = new FakeHeaders(fakeHeaders);
            var chainedHeader3A = new ChainedHeader(fakeHeadersA.Next(), height: 3, totalWork: 3);
            var chainedHeader4A = new ChainedHeader(fakeHeadersA.Next(), height: 4, totalWork: 4);
            var chainedHeader5A = new ChainedHeader(fakeHeadersA.Next(), height: 5, totalWork: 5);

            var fakeHeadersB = new FakeHeaders(fakeHeaders);
            var chainedHeader3B = new ChainedHeader(fakeHeadersB.Next(), height: 3, totalWork: 3);
            var chainedHeader4B = new ChainedHeader(fakeHeadersB.Next(), height: 4, totalWork: 10);

            // mock rules
            var mockRules = new Mock<IBlockchainRules>();
            mockRules.Setup(rules => rules.GenesisChainedHeader).Returns(chainedHeader0);
            kernel.Bind<IBlockchainRules>().ToConstant(mockRules.Object);

            // store genesis block
            var chainedHeaderCache = kernel.Get<ChainedHeaderCache>();
            chainedHeaderCache[chainedHeader0.Hash] = chainedHeader0;

            // initialize the target chain worker
            using (var targetChainWorker = kernel.Get<TargetChainWorker>(new ConstructorArgument("workerConfig", new WorkerConfig(initialNotify: false, minIdleTime: TimeSpan.Zero, maxIdleTime: TimeSpan.MaxValue))))
            {
                // verify initial state
                Assert.AreEqual(null, targetChainWorker.TargetBlock);
                Assert.AreEqual(null, targetChainWorker.TargetChain);

                // monitor event firing
                var workNotifyEvent = new AutoResetEvent(false);
                var workStoppedEvent = new AutoResetEvent(false);
                var onTargetChainChangedCount = 0;

                targetChainWorker.OnNotifyWork += () => workNotifyEvent.Set();
                targetChainWorker.OnWorkStopped += () => workStoppedEvent.Set();
                targetChainWorker.OnTargetChainChanged += () => onTargetChainChangedCount++;

                // start worker and wait for initial chain
                targetChainWorker.Start();
                workNotifyEvent.WaitOne();
                workStoppedEvent.WaitOne();

                // verify chained to block 0
                Assert.AreEqual(chainedHeader0, targetChainWorker.TargetBlock);
                AssertBlockListEquals(new[] { chainedHeader0 }, targetChainWorker.TargetChain.Blocks);
                Assert.AreEqual(1, onTargetChainChangedCount);

                // add block 1
                chainedHeaderCache[chainedHeader1.Hash] = chainedHeader1;

                // wait for worker (chained block addition)
                workNotifyEvent.WaitOne();
                workStoppedEvent.WaitOne();
                // wait for worker (target block changed)
                workNotifyEvent.WaitOneOrFail(1000);
                workStoppedEvent.WaitOneOrFail(1000);

                // verify chained to block 1
                Assert.AreEqual(chainedHeader1, targetChainWorker.TargetBlock);
                AssertBlockListEquals(new[] { chainedHeader0, chainedHeader1 }, targetChainWorker.TargetChain.Blocks);
                Assert.AreEqual(2, onTargetChainChangedCount);

                // add block 2
                chainedHeaderCache[chainedHeader2.Hash] = chainedHeader2;

                // wait for worker (chained block addition)
                workNotifyEvent.WaitOne();
                workStoppedEvent.WaitOne();
                // wait for worker (target block changed)
                workNotifyEvent.WaitOneOrFail(1000);
                workStoppedEvent.WaitOneOrFail(1000);

                // verify chained to block 2
                Assert.AreEqual(chainedHeader2, targetChainWorker.TargetBlock);
                AssertBlockListEquals(new[] { chainedHeader0, chainedHeader1, chainedHeader2 }, targetChainWorker.TargetChain.Blocks);
                Assert.AreEqual(3, onTargetChainChangedCount);

                // add block 3A
                chainedHeaderCache[chainedHeader3A.Hash] = chainedHeader3A;

                // wait for worker (chained block addition)
                workNotifyEvent.WaitOne();
                workStoppedEvent.WaitOne();
                // wait for worker (target block changed)
                workNotifyEvent.WaitOneOrFail(1000);
                workStoppedEvent.WaitOneOrFail(1000);

                // verify chained to block 3A
                Assert.AreEqual(chainedHeader3A, targetChainWorker.TargetBlock);
                AssertBlockListEquals(new[] { chainedHeader0, chainedHeader1, chainedHeader2, chainedHeader3A }, targetChainWorker.TargetChain.Blocks);
                Assert.AreEqual(4, onTargetChainChangedCount);

                // add block 4A
                chainedHeaderCache[chainedHeader4A.Hash] = chainedHeader4A;

                // wait for worker (chained block addition)
                workNotifyEvent.WaitOne();
                workStoppedEvent.WaitOne();
                // wait for worker (target block changed)
                workNotifyEvent.WaitOneOrFail(1000);
                workStoppedEvent.WaitOneOrFail(1000);

                // verify chained to block 4A
                Assert.AreEqual(chainedHeader4A, targetChainWorker.TargetBlock);
                AssertBlockListEquals(new[] { chainedHeader0, chainedHeader1, chainedHeader2, chainedHeader3A, chainedHeader4A }, targetChainWorker.TargetChain.Blocks);
                Assert.AreEqual(5, onTargetChainChangedCount);

                // add block 5A
                chainedHeaderCache[chainedHeader5A.Hash] = chainedHeader5A;

                // wait for worker (chained block addition)
                workNotifyEvent.WaitOne();
                workStoppedEvent.WaitOne();
                // wait for worker (target block changed)
                workNotifyEvent.WaitOneOrFail(1000);
                workStoppedEvent.WaitOneOrFail(1000);

                // verify chained to block 5A
                Assert.AreEqual(chainedHeader5A, targetChainWorker.TargetBlock);
                AssertBlockListEquals(new[] { chainedHeader0, chainedHeader1, chainedHeader2, chainedHeader3A, chainedHeader4A, chainedHeader5A }, targetChainWorker.TargetChain.Blocks);
                Assert.AreEqual(6, onTargetChainChangedCount);

                // add block 3B
                chainedHeaderCache[chainedHeader3B.Hash] = chainedHeader3B;

                // wait for worker (chained block addition)
                workNotifyEvent.WaitOne();
                workStoppedEvent.WaitOne();

                // verify no chaining done
                Assert.AreEqual(chainedHeader5A, targetChainWorker.TargetBlock);
                AssertBlockListEquals(new[] { chainedHeader0, chainedHeader1, chainedHeader2, chainedHeader3A, chainedHeader4A, chainedHeader5A }, targetChainWorker.TargetChain.Blocks);
                Assert.AreEqual(6, onTargetChainChangedCount);

                // add block 4B
                chainedHeaderCache[chainedHeader4B.Hash] = chainedHeader4B;

                // wait for worker (chained block addition)
                workNotifyEvent.WaitOne();
                workStoppedEvent.WaitOne();
                // wait for worker (target block changed)
                workNotifyEvent.WaitOneOrFail(1000);
                workStoppedEvent.WaitOneOrFail(1000);

                // verify chained to block 4B
                Assert.AreEqual(chainedHeader4B, targetChainWorker.TargetBlock);
                AssertBlockListEquals(new[] { chainedHeader0, chainedHeader1, chainedHeader2, chainedHeader3B, chainedHeader4B }, targetChainWorker.TargetChain.Blocks);
                Assert.AreEqual(7, onTargetChainChangedCount);

                // verify no other work was done
                Assert.IsFalse(workNotifyEvent.WaitOne(0));
                Assert.IsFalse(workStoppedEvent.WaitOne(0));
            }
        }

        private static void AssertBlockListEquals(ChainedHeader[] expected, IImmutableList<ChainedHeader> actual)
        {
            Assert.AreEqual(expected.Length, actual.Count);
            for (var i = 0; i < actual.Count; i++)
                Assert.AreEqual(expected[i], actual[i]);
        }
    }

    internal static class TargetChainWorkerTest_ExtensionMethods
    {
        public static void WaitOneOrFail(this WaitHandle handle, int millisecondsTimeout)
        {
            if (!handle.WaitOne(millisecondsTimeout))
                Assert.Fail("WaitHandle hung");
        }
    }
}
