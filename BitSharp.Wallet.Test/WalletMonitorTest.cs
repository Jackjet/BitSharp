﻿using BitSharp.Common;
using BitSharp.Common.ExtensionMethods;
using BitSharp.Core.Domain;
using BitSharp.Core.JsonRpc;
using BitSharp.Wallet;
using BitSharp.Core.Rules;
using BitSharp.Core.Script;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Ninject;
using NLog;
using System.Collections.Concurrent;
using BitSharp.Wallet.Address;
using BitSharp.Core.Test;

namespace BitSharp.Wallet.Test
{
    [TestClass]
    public class WalletMonitorTest
    {
        [TestMethod]
        public void TestMonitorAddress()
        {
            if (Debugger.IsAttached)
                Assert.Inconclusive();

            var sha256 = new SHA256Managed();

            //var publicKey =
            //    "04ea1feff861b51fe3f5f8a3b12d0f4712db80e919548a80839fc47c6a21e66d957e9c5d8cd108c7a2d2324bad71f9904ac0ae7336507d785b17a2c115e427a32f"
            //    .HexToByteArray();

            var publicKey =
                "04f9804cfb86fb17441a6562b07c4ee8f012bdb2da5be022032e4b87100350ccc7c0f4d47078b06c9d22b0ec10bdce4c590e0d01aed618987a6caa8c94d74ee6dc"
                .HexToByteArray().ToImmutableArray();

            var walletMonitor = new WalletMonitor(LogManager.CreateNullLogger());
            walletMonitor.AddAddress(new PublicKeyAddress(publicKey));

            using (var simulator = new MainnetSimulator())
            {
                simulator.CoreDaemon.SubscribeChainStateVisitor(walletMonitor);

                var block9999 = simulator.BlockProvider.GetBlock(9999);

                simulator.AddBlockRange(0, 9999);
                simulator.WaitForDaemon();
                AssertMethods.AssertDaemonAtBlock(9999, block9999.Hash, simulator.CoreDaemon);

                var minedTxOutputs = walletMonitor.Entries.Where(x => x.Type == EnumWalletEntryType.Mine).ToList();
                var receivedTxOutputs = walletMonitor.Entries.Where(x => x.Type == EnumWalletEntryType.Receive).ToList();
                var spentTxOutputs = walletMonitor.Entries.Where(x => x.Type == EnumWalletEntryType.Spend).ToList();

                var actualMinedBtc = minedTxOutputs.Sum(x => (decimal)x.Value) / 100.MILLION();
                var actualReceivedBtc = receivedTxOutputs.Sum(x => (decimal)x.Value) / 100.MILLION();
                var actualSpentBtc = spentTxOutputs.Sum(x => (decimal)x.Value) / 100.MILLION();

                Assert.AreEqual(0, minedTxOutputs.Count);
                Assert.AreEqual(16, receivedTxOutputs.Count);
                Assert.AreEqual(14, spentTxOutputs.Count);
                Assert.AreEqual(0M, actualMinedBtc);
                Assert.AreEqual(569.44M, actualReceivedBtc);
                Assert.AreEqual(536.52M, actualSpentBtc);
            }
        }
    }
}
