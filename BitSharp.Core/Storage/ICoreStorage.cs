﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BitSharp.Core.Storage
{
    public interface ICoreStorage
    {
        BlockHeaderCache BlockHeaderCache { get; }
        ChainedHeaderCache ChainedHeaderCache { get; }
        BlockCache BlockCache { get; }
        InvalidBlockCache InvalidBlockCache { get; }

        //TODO move to utxo storage
        SpentTransactionsCache SpentTransactionsCache { get; }
        SpentOutputsCache SpentOutputsCache { get; }

        //TODO move to indexed storage
        BlockTxHashesCache BlockTxHashesCache { get; }
        TransactionCache TransactionCache { get; }
    }
}
