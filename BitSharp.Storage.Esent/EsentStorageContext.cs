﻿using BitSharp.Common;
using BitSharp.Common.ExtensionMethods;
using BitSharp.Data;
using BitSharp.Storage.Esent;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BitSharp.Storage.Esent
{
    public class EsentStorageContext : IStorageContext
    {
        private readonly string baseDirectory;
        private readonly BlockHeaderStorage _blockHeaderStorage;
        private readonly BlockTxHashesStorage _blockTxHashesStorage;
        private readonly TransactionStorage _transactionStorage;
        private readonly ChainedBlockStorage _chainedBlockStorage;

        public EsentStorageContext(string baseDirectory)
        {
            this.baseDirectory = baseDirectory;
            this._blockHeaderStorage = new BlockHeaderStorage(this);
            this._blockTxHashesStorage = new BlockTxHashesStorage(this);
            this._transactionStorage = new TransactionStorage(this);
            this._chainedBlockStorage = new ChainedBlockStorage(this);
        }

        public BlockHeaderStorage BlockHeaderStorage { get { return this._blockHeaderStorage; } }

        public BlockTxHashesStorage BlockTxHashesStorage { get { return this._blockTxHashesStorage; } }

        public TransactionStorage Transactionstorage { get { return this._transactionStorage; } }

        public ChainedBlockStorage ChainedBlockStorage { get { return this._chainedBlockStorage; } }

        internal string BaseDirectory { get { return this.baseDirectory; } }

        IBoundedStorage<UInt256, BlockHeader> IStorageContext.BlockHeaderStorage { get { return this._blockHeaderStorage; } }

        IBoundedStorage<UInt256, ChainedBlock> IStorageContext.ChainedBlockStorage { get { return this._chainedBlockStorage; } }

        IBoundedStorage<UInt256, IImmutableList<UInt256>> IStorageContext.BlockTxHashesStorage { get { return this._blockTxHashesStorage; } }

        IUnboundedStorage<UInt256, Transaction> IStorageContext.TransactionStorage { get { return this._transactionStorage; } }

        public IEnumerable<ChainedBlock> SelectMaxTotalWorkBlocks()
        {
            return this.ChainedBlockStorage.SelectMaxTotalWorkBlocks();
        }

        public IUtxoBuilderStorage ToUtxoBuilder(Utxo utxo)
        {
            return new PersistentUtxoBuilderStorage(utxo);
        }

        public void Dispose()
        {
            new IDisposable[]
            {
                this._blockHeaderStorage,
                this._chainedBlockStorage,
                this._blockTxHashesStorage,
                this._transactionStorage
            }.DisposeList();
        }
    
}
}
