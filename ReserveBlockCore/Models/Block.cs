﻿using ReserveBlockCore.Data;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;


namespace ReserveBlockCore.Models
{
    public class Block
    {
		public long Height { get; set; }
		public string ChainRefId { get; set; }
		public long Timestamp { get; set; }
		public string Hash { get; set; }
		public string PrevHash { get; set; }
		public string MerkleRoot { get; set; }
		public string StateRoot { get; set; }
		public string Validator { get; set; }
		public string ValidatorSignature { get; set; }
		public string ValidatorAnswer { get; set; }
		public decimal TotalReward { get; set; }
		public int TotalValidators { get; set; }
		public decimal TotalAmount { get; set; }
		public int Version { get; set; }
		public int NumOfTx { get; set; }
		public long Size { get; set; }
		public int BCraftTime { get; set; }
		public string AdjudicatorSignature { get; set; }

		public IList<Transaction> Transactions { get; set; }
		//Methods
		public void Build()
		{
            Version = BlockVersionUtility.GetBlockVersion(Height); //have this version increase if invalid/malformed block is submitted to auto branch and avoid need for fork.
            NumOfTx = Transactions.Count;
            TotalAmount = GetTotalAmount();
            TotalReward = Globals.LastBlock.Height != -1 ? GetTotalFees() : 0M;
            MerkleRoot = GetMerkleRoot();
            PrevHash = GetPreviousHash(); //This is done because chain starting there won't be a previous hash. 
            Hash = GetBlockHash();
            StateRoot = GetStateRoot();
		}
		public void Rebuild(Block block)
        {
			Version = BlockVersionUtility.GetBlockVersion(Height);  //have this version increase if invalid/malformed block is submitted to auto branch and avoid need for fork.
			NumOfTx = Transactions.Count;
			TotalAmount = GetTotalAmount();
			TotalReward = GetTotalFees();
			MerkleRoot = GetMerkleRoot();
			PrevHash = Globals.LastBlock.Hash;
			Hash = GetBlockHash();
			StateRoot = GetStateRoot();
		}

		public string GetPreviousHash()
		{
			if (Globals.LastBlock.Height == -1)
				return "Genesis Block";

			if(Globals.LastBlock.Height + 1 == Height)
				return Globals.LastBlock.Hash;

			if(Globals.NetworkBlockQueue.Count() > 0)
			{
                if (Globals.NetworkBlockQueue.TryGetValue(Height - 1, out var block))
                {
                    return block.Hash;
                }
            }

			return "0";
        }
		public int NumberOfTransactions
		{
			get { return Transactions.Count(); }
		}
		private decimal GetTotalFees()
		{
			var totFee = Transactions.AsEnumerable().Sum(x => x.Fee);
			return totFee;
		}
		public static LiteDB.ILiteCollection<Block> GetBlocks()
		{
			var block = DbContext.DB.GetCollection<Block>(DbContext.RSRV_BLOCKS);
			return block;
		}
		private decimal GetTotalAmount()
		{
			var totalAmount = Transactions.AsEnumerable().Sum(x => x.Amount);
			return totalAmount;
		}
		public string GetBlockHash()
		{
			var strSum = Version + PrevHash + MerkleRoot + Timestamp + NumOfTx + Validator + TotalValidators.ToString() + ValidatorAnswer + ChainRefId;
			var hash = HashingService.GenerateHash(strSum);
			return hash;
		}
		public string GetStateRoot()
		{
			try
			{
                var strSum = Hash.Substring(0, 6) + PrevHash.Substring(0, 6) + MerkleRoot.Substring(0, 6) + Timestamp;
                var hash = HashingService.GenerateHash(strSum);
                return hash;
            }
			
			catch (Exception ex)
			{
				return "";
			}
		}
		private string GetMerkleRoot()
		{
			// List<Transaction> txList = JsonConvert.DeserializeObject<List<Transaction>>(jsonTxs);
			var txsHash = new List<string>();

			Transactions.ToList().ForEach(x => { txsHash.Add(x.Hash); });

			var hashRoot = MerkleService.CreateMerkleRoot(txsHash.ToArray());
			return hashRoot;
		}
	}
}
