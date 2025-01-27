﻿using DBriize;
using Microsoft.Extensions.Logging;
using System.Linq;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.JsonConverters;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using System.Threading.Tasks;
using System.Threading;
using NBitcoin.DataEncoders;
using DBriize.Utils;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using DBriize.Exceptions;
using NBXplorer.Logging;
using NBXplorer.Configuration;
using static NBXplorer.RepositoryProvider;
using static NBXplorer.Repository;
using Newtonsoft.Json.Linq;

namespace NBXplorer
{
	public class GenerateAddressQuery
	{
		public GenerateAddressQuery()
		{

		}
		public GenerateAddressQuery(int? minAddresses, int? maxAddresses)
		{
			MinAddresses = minAddresses;
			MaxAddresses = maxAddresses;
		}
		public int? MinAddresses { get; set; }
		public int? MaxAddresses { get; set; }
	}
	public class RepositoryProvider
	{
		DBriizeEngine _Engine;
		Dictionary<string, Repository> _Repositories = new Dictionary<string, Repository>();
		private readonly KeyPathTemplates keyPathTemplates;
		ExplorerConfiguration _Configuration;
		public RepositoryProvider(NBXplorerNetworkProvider networks, KeyPathTemplates keyPathTemplates, ExplorerConfiguration configuration)
		{
			this.keyPathTemplates = keyPathTemplates;
			_Configuration = configuration;
			var directory = Path.Combine(configuration.DataDir, "db");
			if (!Directory.Exists(directory))
				Directory.CreateDirectory(directory);

			int tried = 0;
		retry:
			try
			{
				_Engine = new DBriizeEngine(new DBriizeConfiguration()
				{
					DBriizeDataFolderName = directory
				});
			}
			catch when (tried < 10)
			{
				tried++;
				Thread.Sleep(tried * 500);
				goto retry;
			}
			foreach (var net in networks.GetAll())
			{
				var settings = GetChainSetting(net);
				if (settings != null)
				{
					var repo = new Repository(_Engine, net, keyPathTemplates);
					repo.MaxPoolSize = configuration.MaxGapSize;
					repo.MinPoolSize = configuration.MinGapSize;
					_Repositories.Add(net.CryptoCode, repo);
				}
			}
		}

		private ChainConfiguration GetChainSetting(NBXplorerNetwork net)
		{
			return _Configuration.ChainConfigurations.FirstOrDefault(c => c.CryptoCode == net.CryptoCode);
		}

		public async Task StartAsync()
		{
			await Task.WhenAll(_Repositories.Select(kv => kv.Value.StartAsync()).ToArray());
			foreach	(var repo in _Repositories.Select(kv => kv.Value))
			{
				if(GetChainSetting(repo.Network) is ChainConfiguration chainConf && chainConf.Rescan)
				{
					Logs.Configuration.LogInformation($"{repo.Network.CryptoCode}: Rescanning the chain...");
					await repo.SetIndexProgress(null);
				}
			}
		}

		public IEnumerable<Repository> GetAll()
		{
			return _Repositories.Values;
		}

		public Repository GetRepository(NBXplorerNetwork network)
		{
			_Repositories.TryGetValue(network.CryptoCode, out Repository repository);
			return repository;
		}

		public async Task DisposeAsync()
		{
			await Task.WhenAll(_Repositories.Select(kv => kv.Value.DisposeAsync()).ToArray());
			_Engine.Dispose();
		}
	}

	public class Repository
	{


		public async Task Ping()
		{
			await _TxContext.DoAsync((tx) =>
			{
			});
		}

		public async Task CancelReservation(DerivationStrategyBase strategy, KeyPath[] keyPaths)
		{
			if (keyPaths.Length == 0)
				return;
			await _TxContext.DoAsync(tx =>
			{
				bool needCommit = false;
				var featuresPerKeyPaths = keyPathTemplates.GetSupportedDerivationFeatures()
				.Select(f => (Feature: f, KeyPathTemplate: keyPathTemplates.GetKeyPathTemplate(f)))
				.ToDictionary(o => o.KeyPathTemplate, o => o.Feature);

				var groups = keyPaths.Where(k => k.Indexes.Length > 0).GroupBy(k => keyPathTemplates.GetKeyPathTemplate(k));
				foreach (var group in groups)
				{
					if (featuresPerKeyPaths.TryGetValue(group.Key, out DerivationFeature feature))
					{
						var reserved = GetReservedKeysIndex(tx, strategy, feature);
						var available = GetAvailableKeysIndex(tx, strategy, feature);
						foreach (var keyPath in group)
						{
							var key = (int)keyPath.Indexes.Last();
							var data = reserved.SelectBytes(key);
							if (data == null)
								continue;
							reserved.RemoveKey(key);
							available.Insert(key, data);
							needCommit = true;
						}
					}
				}
				if (needCommit)
					tx.Commit();
			});
		}

		class Index
		{
			DBriize.Transactions.Transaction tx;
			public Index(DBriize.Transactions.Transaction tx, string tableName, string primaryKey)
			{
				TableName = tableName;
				PrimaryKey = primaryKey;
				this.tx = tx;
			}


			public string TableName
			{
				get; set;
			}
			public string PrimaryKey
			{
				get; set;
			}

			public byte[] SelectBytes(int index)
			{
				var bytes = tx.Select<string, byte[]>(TableName, $"{PrimaryKey}-{index:D10}");
				if (bytes == null || !bytes.Exists)
					return null;
				return bytes.Value;
			}

			public void RemoveKey(string index)
			{
				tx.RemoveKey(TableName, $"{PrimaryKey}-{index}");
			}

			public void Insert(string key, byte[] value)
			{
				tx.Insert(TableName, $"{PrimaryKey}-{key}", value);
			}

			public (string Key, byte[] Value)[] SelectForwardSkip(int n, string startWith = null)
			{
				if (startWith == null)
					startWith = PrimaryKey;
				else
					startWith = $"{PrimaryKey}-{startWith}";
				return tx.SelectForwardStartsWith<string, byte[]>(TableName, startWith).Skip(n).Select(c => (c.Key, c.Value)).ToArray();
			}

			public (long Key, byte[] Value)[] SelectFrom(long key, int? limit)
			{
				return tx.SelectForwardStartFrom<string, byte[]>(TableName, $"{PrimaryKey}-{key:D20}", false)
						.Take(limit == null ? Int32.MaxValue : limit.Value)
						.Where(r => r.Exists && r.Value != null)
						.Select(r => (ExtractLong(r.Key), r.Value))
						.ToArray();
			}

			private long ExtractLong(string key)
			{
				var span = key.AsSpan();
				var sep = span.LastIndexOf('-');
				span = span.Slice(sep + 1);
				return long.Parse(span);
			}

			public int Count()
			{
				return tx.SelectForwardStartsWith<string, byte[]>(TableName, PrimaryKey).Count();
			}

			public void Insert(int key, byte[] value)
			{
				Insert($"{key:D10}", value);
			}
			public void Insert(long key, byte[] value)
			{
				Insert($"{key:D20}", value);
			}
			public void RemoveKey(int index)
			{
				RemoveKey($"{index:D10}");
			}
			public int? SelectInt(int index)
			{
				var bytes = SelectBytes(index);
				if (bytes == null)
					return null;
				bytes[0] = (byte)(bytes[0] & ~0x80);
				return (int)NBitcoin.Utils.ToUInt32(bytes, false);
			}
			public long? SelectLong(int index)
			{
				var bytes = SelectBytes(index);
				if (bytes == null)
					return null;
				bytes[0] = (byte)(bytes[0] & ~0x80);
				return (int)NBitcoin.Utils.ToUInt64(bytes, false);
			}
			public void Insert(int key, int value)
			{
				var bytes = NBitcoin.Utils.ToBytes((uint)value, false);
				Insert(key, bytes);
			}
			public void Insert(int key, long value)
			{
				var bytes = NBitcoin.Utils.ToBytes((ulong)value, false);
				Insert(key, bytes);
			}
		}

		Index GetAvailableKeysIndex(DBriize.Transactions.Transaction tx, DerivationStrategyBase trackedSource, DerivationFeature feature)
		{
			return new Index(tx, $"{_Suffix}AvailableKeys", $"{trackedSource.GetHash()}-{feature}");
		}

		Index GetScriptsIndex(DBriize.Transactions.Transaction tx, Script scriptPubKey)
		{
			return new Index(tx, $"{_Suffix}Scripts", $"{scriptPubKey.Hash}");
		}

		Index GetHighestPathIndex(DBriize.Transactions.Transaction tx, DerivationStrategyBase strategy, DerivationFeature feature)
		{
			return new Index(tx, $"{_Suffix}HighestPath", $"{strategy.GetHash()}-{feature}");
		}

		Index GetReservedKeysIndex(DBriize.Transactions.Transaction tx, DerivationStrategyBase strategy, DerivationFeature feature)
		{
			return new Index(tx, $"{_Suffix}ReservedKeys", $"{strategy.GetHash()}-{feature}");
		}

		Index GetTransactionsIndex(DBriize.Transactions.Transaction tx, TrackedSource trackedSource)
		{
			return new Index(tx, $"{_Suffix}Transactions", $"{trackedSource.GetHash()}");
		}

		Index GetMetadataIndex(DBriize.Transactions.Transaction tx, TrackedSource trackedSource)
		{
			return new Index(tx, $"{_Suffix}Metadata", $"{trackedSource.GetHash()}");
		}

		Index GetEventsIndex(DBriize.Transactions.Transaction tx)
		{
			return new Index(tx, $"{_Suffix}Events", string.Empty);
		}

		NBXplorerNetwork _Network;
		private readonly KeyPathTemplates keyPathTemplates;

		public NBXplorerNetwork Network
		{
			get
			{
				return _Network;
			}
		}

		DBriizeTransactionContext _TxContext;
		internal Repository(DBriizeEngine engine, NBXplorerNetwork network, KeyPathTemplates keyPathTemplates)
		{
			if (network == null)
				throw new ArgumentNullException(nameof(network));
			_Network = network;
			this.keyPathTemplates = keyPathTemplates;
			Serializer = new Serializer(_Network);
			_Network = network;
			_TxContext = new DBriizeTransactionContext(engine);
			_TxContext.UnhandledException += (s, ex) =>
			{
				Logs.Explorer.LogCritical(ex, $"{network.CryptoCode}: Unhandled exception in the repository");
			};
			_Suffix = network.CryptoCode == "BTC" ? "" : network.CryptoCode;
		}

		public Task StartAsync()
		{
			return _TxContext.StartAsync();
		}
		public Task DisposeAsync()
		{
			return _TxContext.DisposeAsync();
		}

		public string _Suffix;
		public Task<BlockLocator> GetIndexProgress()
		{
			return _TxContext.DoAsync<BlockLocator>(tx =>
			{
				tx.ValuesLazyLoadingIsOn = false;
				var existingRow = tx.Select<string, byte[]>($"{_Suffix}IndexProgress", "");
				if (existingRow == null || !existingRow.Exists)
					return null;
				BlockLocator locator = new BlockLocator();
				locator.FromBytes(existingRow.Value);
				return locator;
			});
		}

		public Task SetIndexProgress(BlockLocator locator)
		{
			return _TxContext.DoAsync(tx =>
			{
				if (locator == null)
					tx.RemoveKey($"{_Suffix}IndexProgress", "");
				else
					tx.Insert($"{_Suffix}IndexProgress", "", locator.ToBytes());
				tx.Commit();
			});
		}

		public Task<KeyPathInformation> GetUnused(DerivationStrategyBase strategy, DerivationFeature derivationFeature, int n, bool reserve)
		{
			return _TxContext.DoAsync<KeyPathInformation>((tx) =>
			{
				tx.ValuesLazyLoadingIsOn = false;
				var availableTable = GetAvailableKeysIndex(tx, strategy, derivationFeature);
				var reservedTable = GetReservedKeysIndex(tx, strategy, derivationFeature);
				var rows = availableTable.SelectForwardSkip(n);
				if (rows.Length == 0)
					return null;
				var keyInfo = ToObject<KeyPathInformation>(rows[0].Value).AddAddress(Network.NBitcoinNetwork);
				if (reserve)
				{
					availableTable.RemoveKey(keyInfo.GetIndex());
					reservedTable.Insert(keyInfo.GetIndex(), rows[0].Value);
					tx.Commit();
				}
				return keyInfo;
			});
		}

		int GetAddressToGenerateCount(DBriize.Transactions.Transaction tx, DerivationStrategyBase strategy, DerivationFeature derivationFeature)
		{
			var availableTable = GetAvailableKeysIndex(tx, strategy, derivationFeature);
			var currentlyAvailable = availableTable.Count();
			if (currentlyAvailable >= MinPoolSize)
				return 0;
			return Math.Max(0, MaxPoolSize - currentlyAvailable);
		}

		private void RefillAvailable(DBriize.Transactions.Transaction tx, DerivationStrategyBase strategy, DerivationFeature derivationFeature, int toGenerate)
		{
			if (toGenerate <= 0)
				return;
			var availableTable = GetAvailableKeysIndex(tx, strategy, derivationFeature);
			var highestTable = GetHighestPathIndex(tx, strategy, derivationFeature);
			int highestGenerated = highestTable.SelectInt(0) ?? -1;
			var feature = strategy.GetLineFor(keyPathTemplates.GetKeyPathTemplate(derivationFeature));

			KeyPathInformation[] keyPathInformations = new KeyPathInformation[toGenerate];
			Parallel.For(0, toGenerate, i =>
			{
				var index = highestGenerated + i + 1;
				var derivation = feature.Derive((uint)index);
				var info = new KeyPathInformation()
				{
					ScriptPubKey = derivation.ScriptPubKey,
					Redeem = derivation.Redeem,
					TrackedSource = new DerivationSchemeTrackedSource(strategy),
					DerivationStrategy = strategy,
					Feature = derivationFeature,
					KeyPath = keyPathTemplates.GetKeyPathTemplate(derivationFeature).GetKeyPath(index, false)
				};
				keyPathInformations[i] = info;
			});
			for (int i = 0; i < toGenerate; i++)
			{
				var index = highestGenerated + i + 1;
				var info = keyPathInformations[i];
				var bytes = ToBytes(info);
				GetScriptsIndex(tx, info.ScriptPubKey).Insert($"{strategy.GetHash()}-{derivationFeature}", bytes);
				availableTable.Insert(index, bytes);
			}
			highestTable.Insert(0, highestGenerated + toGenerate);
			tx.Commit();
		}

		public Task<long> SaveEvent(NewEventBase evt)
		{
			// Fetch the lastEventId on row 0
			// Increment it,
			// Insert event
			return _TxContext.DoAsync((tx) =>
			{
				var idx = GetEventsIndex(tx);
				var lastEventIndexMaybe = idx.SelectLong(0);
				var lastEventIndex = lastEventIndexMaybe.HasValue ? lastEventIndexMaybe.Value + 1 : 1;
				idx.Insert(0, lastEventIndex);
				idx.Insert(lastEventIndex, this.ToBytes(evt.ToJObject(Serializer.Settings)));
				tx.Commit();
				lastKnownEventIndex = lastEventIndex;
				return lastEventIndex;
			});
		}
		long lastKnownEventIndex = -1;
		public Task<IList<NewEventBase>> GetEvents(long lastEventId, int? limit = null)
		{
			if (lastEventId < 1 && limit.HasValue && limit.Value != int.MaxValue)
				limit = limit.Value + 1; // The row with key 0 holds the lastEventId
			return _TxContext.DoAsync((tx) =>
			{
				if (lastKnownEventIndex != -1 && lastKnownEventIndex == lastEventId)
					return new List<NewEventBase>();
				tx.ValuesLazyLoadingIsOn = false;
				var idx = GetEventsIndex(tx);
				var query = idx.SelectFrom(lastEventId, limit);
				IList<NewEventBase> evts = new List<NewEventBase>(query.Length);
				foreach (var value in query)
				{
					if (value.Key == 0) // Last Index
						continue;
					var evt = NewEventBase.ParseEvent(ToObject<JObject>(value.Value), Serializer.Settings);
					evt.EventId = value.Key;
					evts.Add(evt);
				}
				return evts;
			});
		}

		public Task SaveKeyInformations(KeyPathInformation[] keyPathInformations)
		{
			return _TxContext.DoAsync((tx) =>
			{
				foreach (var info in keyPathInformations)
				{
					var bytes = ToBytes(info);
					GetScriptsIndex(tx, info.ScriptPubKey).Insert($"{info.DerivationStrategy.GetHash()}-{info.Feature}", bytes);
				}
				tx.Commit();
			});
		}

		public Task Track(IDestination address)
		{
			return _TxContext.DoAsync((tx) =>
			{
				var info = new KeyPathInformation()
				{
					ScriptPubKey = address.ScriptPubKey,
					TrackedSource = (TrackedSource)address
				};
				var bytes = ToBytes(info);
				GetScriptsIndex(tx, address.ScriptPubKey).Insert(address.ScriptPubKey.Hash.ToString(), bytes);
				tx.Commit();
			});
		}

		public Task<int> GenerateAddresses(DerivationStrategyBase strategy, DerivationFeature derivationFeature, int maxAddresses)
		{
			return GenerateAddresses(strategy, derivationFeature, new GenerateAddressQuery(null, maxAddresses));
		}
		public Task<int> GenerateAddresses(DerivationStrategyBase strategy, DerivationFeature derivationFeature, GenerateAddressQuery query = null)
		{
			query = query ?? new GenerateAddressQuery();
			return _TxContext.DoAsync((tx) =>
			{
				var toGenerate = GetAddressToGenerateCount(tx, strategy, derivationFeature);
				if (query.MaxAddresses is int max)
					toGenerate = Math.Min(max, toGenerate);
				if (query.MinAddresses is int min)
					toGenerate = Math.Max(min, toGenerate);
				RefillAvailable(tx, strategy, derivationFeature, toGenerate);
				return toGenerate;
			});
		}

		class TimeStampedTransaction : IBitcoinSerializable
		{

			public TimeStampedTransaction()
			{

			}
			public TimeStampedTransaction(Network network, byte[] hex)
			{
				var stream = new BitcoinStream(hex);
				stream.ConsensusFactory = network.Consensus.ConsensusFactory;
				this.ReadWrite(stream);
			}

			public TimeStampedTransaction(Transaction tx, ulong timestamp)
			{
				_TimeStamp = timestamp;
				_Transaction = tx;
			}
			Transaction _Transaction;
			public Transaction Transaction
			{
				get
				{
					return _Transaction;
				}
				set
				{
					_Transaction = value;
				}
			}


			ulong _TimeStamp = 0;
			public ulong TimeStamp
			{
				get
				{
					return _TimeStamp;
				}
				set
				{
					_TimeStamp = value;
				}
			}

			public void ReadWrite(BitcoinStream stream)
			{
				stream.ReadWrite(ref _Transaction);

				// So we don't crash on transactions indexed on old versions
				if (stream.Serializing || stream.Inner.Position != stream.Inner.Length)
					stream.ReadWrite(ref _TimeStamp);
			}
		}

		public int BatchSize
		{
			get; set;
		} = 100;
		public async Task<List<SavedTransaction>> SaveTransactions(DateTimeOffset now, NBitcoin.Transaction[] transactions, uint256 blockHash)
		{
			var result = new List<SavedTransaction>();
			transactions = transactions.Distinct().ToArray();
			if (transactions.Length == 0)
				return result;
			foreach (var batch in transactions.Batch(BatchSize))
			{
				await _TxContext.DoAsync(tx =>
				{
					var date = NBitcoin.Utils.DateTimeToUnixTime(now);
					foreach (var btx in batch)
					{
						var timestamped = new TimeStampedTransaction(btx, date);
						var key = blockHash == null ? "0" : blockHash.ToString();
						var value = timestamped.ToBytes();
						tx.Insert($"{_Suffix}tx-" + btx.GetHash().ToString(), key, value);
						result.Add(ToSavedTransaction(Network.NBitcoinNetwork, key, value));
					}
					tx.Commit();
				});
			}
			return result;
		}

		public class SavedTransaction
		{
			public NBitcoin.Transaction Transaction
			{
				get; set;
			}
			public uint256 BlockHash
			{
				get; set;
			}
			public DateTimeOffset Timestamp
			{
				get;
				set;
			}
		}

		public async Task<SavedTransaction[]> GetSavedTransactions(uint256 txid)
		{
			List<SavedTransaction> saved = new List<SavedTransaction>();
			await _TxContext.DoAsync(tx =>
			{
				foreach (var row in tx.SelectForward<string, byte[]>($"{_Suffix}tx-" + txid.ToString()))
				{
					SavedTransaction t = ToSavedTransaction(Network.NBitcoinNetwork, row.Key, row.Value);
					saved.Add(t);
				}
			});
			return saved.ToArray();
		}

		private static SavedTransaction ToSavedTransaction(Network network, string key, byte[] value)
		{
			SavedTransaction t = new SavedTransaction();
			if (key.Length != 1)
				t.BlockHash = new uint256(key);
			var timeStamped = new TimeStampedTransaction(network, value);
			t.Transaction = timeStamped.Transaction;
			t.Timestamp = NBitcoin.Utils.UnixTimeToDateTime(timeStamped.TimeStamp);
			t.Transaction.PrecomputeHash(true, false);
			return t;
		}
		public async Task<MultiValueDictionary<Script, KeyPathInformation>> GetKeyInformations(Script[] scripts)
		{
			MultiValueDictionary<Script, KeyPathInformation> result = new MultiValueDictionary<Script, KeyPathInformation>();
			if (scripts.Length == 0)
				return result;
			foreach (var batch in scripts.Batch(BatchSize))
			{
				await _TxContext.DoAsync(tx =>
				{
					tx.ValuesLazyLoadingIsOn = false;
					foreach (var script in batch)
					{
						var table = GetScriptsIndex(tx, script);
						var keyInfos = table.SelectForwardSkip(0)
											.Select(r => ToObject<KeyPathInformation>(r.Value).AddAddress(Network.NBitcoinNetwork))
											// Because xpub are mutable (several xpub map to same script)
											// an attacker could generate lot's of xpub mapping to the same script
											// and this would blow up here. This we take only 5 results max.
											.Take(5)
											.ToArray();
						result.AddRange(script, keyInfos);
					}
				});
			}
			return result;
		}

		public Serializer Serializer
		{
			get; private set;
		}
		private T ToObject<T>(byte[] value)
		{
			var result = Serializer.ToObject<T>(Unzip(value));

			// For back compat, some old serialized KeyPathInformation do not have TrackedSource property set
			if (result is KeyPathInformation keyPathInfo)
			{
				if (keyPathInfo.TrackedSource == null && keyPathInfo.DerivationStrategy != null)
				{
					keyPathInfo.TrackedSource = new DerivationSchemeTrackedSource(keyPathInfo.DerivationStrategy);
				}
			}
			return result;
		}
		private byte[] ToBytes<T>(T obj)
		{
			return Zip(Serializer.ToString<T>(obj));
		}

		private byte[] Zip(string unzipped)
		{
			MemoryStream ms = new MemoryStream();
			using (GZipStream gzip = new GZipStream(ms, CompressionMode.Compress))
			{
				StreamWriter writer = new StreamWriter(gzip, Encoding.UTF8);
				writer.Write(unzipped);
				writer.Flush();
			}
			return ms.ToArray();
		}
		private string Unzip(byte[] bytes)
		{
			MemoryStream ms = new MemoryStream(bytes);
			using (GZipStream gzip = new GZipStream(ms, CompressionMode.Decompress))
			{
				StreamReader reader = new StreamReader(gzip, Encoding.UTF8);
				var unzipped = reader.ReadToEnd();
				return unzipped;
			}
		}

		public int MinPoolSize
		{
			get; set;
		} = 20;
		public int MaxPoolSize
		{
			get; set;
		} = 30;

		public async Task<TrackedTransaction[]> GetTransactions(TrackedSource trackedSource, uint256 txId = null)
		{

			bool needUpdate = false;
			Dictionary<uint256, long> firstSeenList = new Dictionary<uint256, long>();

			var transactions = await _TxContext.DoAsync(tx =>
			{
				var table = GetTransactionsIndex(tx, trackedSource);
				tx.ValuesLazyLoadingIsOn = false;
				var result = new List<TransactionMatchData>();
				foreach (var row in table.SelectForwardSkip(0, txId?.ToString()))
				{
					MemoryStream ms = new MemoryStream(row.Value);
					BitcoinStream bs = new BitcoinStream(ms, false);
					bs.ConsensusFactory = Network.NBitcoinNetwork.Consensus.ConsensusFactory;
					TransactionMatchData data = new TransactionMatchData(TrackedTransactionKey.Parse(row.Key));
					data.ReadWrite(bs);
					result.Add(data);

					if (data.NeedUpdate)
						needUpdate = true;

					long firstSeen;
					if (firstSeenList.TryGetValue(data.Key.TxId, out firstSeen))
					{
						if (firstSeen > data.FirstSeenTickCount)
							firstSeenList[data.Key.TxId] = firstSeen;
					}
					else
					{
						firstSeenList.Add(data.Key.TxId, data.FirstSeenTickCount);
					}

				}
				return result;
			});

			TransactionMatchData previousConfirmed = null;
			foreach (var tx in transactions)
			{
				if (tx.Key.BlockHash != null)
				{
					if (!tx.Key.IsPruned)
					{
						previousConfirmed = tx;
					}
					else if (previousConfirmed != null &&
							 tx.Key.TxId == previousConfirmed.Key.TxId &&
							 tx.Key.BlockHash == previousConfirmed.Key.BlockHash)
					{
						needUpdate = true;
						tx.NeedRemove = true;

						foreach (var kv in tx.KnownKeyPathMapping)
						{
							// The pruned transaction has more info about owned utxo than the unpruned, we need to update the unpruned
							if (previousConfirmed.KnownKeyPathMapping.TryAdd(kv.Key, kv.Value))
								previousConfirmed.NeedUpdate = true;
						}
					}
					else
					{
						previousConfirmed = null;
					}
				}

				if (tx.FirstSeenTickCount != firstSeenList[tx.Key.TxId])
				{
					needUpdate = true;
					tx.NeedUpdate = true;
					tx.FirstSeenTickCount = firstSeenList[tx.Key.TxId];
				}
			}
			if (needUpdate)
			{
				// This is legacy data, need an update
				foreach (var data in transactions.Where(t => t.NeedUpdate && t.KnownKeyPathMapping == null))
				{
					data.KnownKeyPathMapping = (await this.GetMatches(data.Transaction, data.Key.BlockHash, DateTimeOffset.UtcNow, false))
											  .Where(m => m.TrackedSource.Equals(trackedSource))
											  .Select(m => m.KnownKeyPathMapping)
											  .First();
				}
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
				// This can be eventually consistent, let's not waste one round trip waiting for this
				_TxContext.DoAsync(tx =>
				{
					var table = GetTransactionsIndex(tx, trackedSource);
					foreach (var data in transactions.Where(t => t.NeedUpdate))
					{
						table.Insert(data.GetRowKey(), data.ToBytes());
					}
					foreach (var data in transactions.Where(t => t.NeedRemove))
					{
						table.RemoveKey(data.Key.ToString());
					}
					tx.Commit();
				});
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
			}
			return transactions.Where(tt => !tt.NeedRemove).Select(c => c.ToTrackedTransaction(trackedSource)).ToArray();
		}

		public class TransactionMiniKeyInformation : IBitcoinSerializable
		{
			public TransactionMiniKeyInformation()
			{

			}
			public TransactionMiniKeyInformation(KeyPathInformation keyInformation)
			{
				_KeyPath = keyInformation.KeyPath;
				_ScriptPubKey = keyInformation.ScriptPubKey;
			}



			Script _ScriptPubKey;
			public Script ScriptPubKey
			{
				get
				{
					return _ScriptPubKey;
				}
				set
				{
					_ScriptPubKey = value;
				}
			}

			KeyPath _KeyPath;
			public KeyPath KeyPath
			{
				get
				{
					return _KeyPath;
				}
				set
				{
					_KeyPath = value;
				}
			}

			public void ReadWrite(BitcoinStream stream)
			{
				stream.ReadWrite(ref _ScriptPubKey);
				if (stream.Serializing)
				{
					if (_KeyPath == null)
					{
						stream.ReadWrite((byte)0);
					}
					else
					{
						stream.ReadWrite((byte)_KeyPath.Indexes.Length);
						foreach (var index in _KeyPath.Indexes)
						{
							stream.ReadWrite(index);
						}
					}
				}
				else
				{
					byte len = 0;
					stream.ReadWrite(ref len);
					var indexes = new uint[len];
					for (int i = 0; i < len; i++)
					{
						uint index = 0;
						stream.ReadWrite(ref index);
						indexes[i] = index;
					}
					if (len != 0)
						_KeyPath = new KeyPath(indexes);
				}
			}
		}

		public class TransactionMiniMatch : IBitcoinSerializable
		{

			public TransactionMiniMatch()
			{
				_Outputs = Array.Empty<TransactionMiniKeyInformation>();
				_Inputs = Array.Empty<TransactionMiniKeyInformation>();
			}

			TransactionMiniKeyInformation[] _Outputs;
			public TransactionMiniKeyInformation[] Outputs
			{
				get
				{
					return _Outputs;
				}
				set
				{
					_Outputs = value;
				}
			}


			TransactionMiniKeyInformation[] _Inputs;
			public TransactionMiniKeyInformation[] Inputs
			{
				get
				{
					return _Inputs;
				}
				set
				{
					_Inputs = value;
				}
			}

			public void ReadWrite(BitcoinStream stream)
			{
				stream.ReadWrite(ref _Inputs);
				stream.ReadWrite(ref _Outputs);
			}
		}

		class TransactionMatchData : IBitcoinSerializable
		{
			class CoinData : IBitcoinSerializable
			{
				public CoinData()
				{

				}
				public CoinData(uint index, TxOut txOut)
				{
					_Index = index;
					_TxOut = txOut;
				}
				private uint _Index;
				public uint Index
				{
					get
					{
						return _Index;
					}
				}
				private TxOut _TxOut;
				public TxOut TxOut
				{
					get
					{
						return _TxOut;
					}
				}

				public void ReadWrite(BitcoinStream stream)
				{
					stream.ReadWriteAsVarInt(ref _Index);
					stream.ReadWrite(ref _TxOut);
				}
			}
			public TransactionMatchData(TrackedTransactionKey key)
			{
				if (key == null)
					throw new ArgumentNullException(nameof(key));
				Key = key;
			}
			public TransactionMatchData(TrackedTransaction trackedTransaction)
			{
				if (trackedTransaction == null)
					throw new ArgumentNullException(nameof(trackedTransaction));
				Key = trackedTransaction.Key;
				Transaction = trackedTransaction.Transaction;
				FirstSeenTickCount = trackedTransaction.FirstSeen.Ticks;
				TickCount = trackedTransaction.Inserted.Ticks;
				KnownKeyPathMapping = trackedTransaction.KnownKeyPathMapping;
				if (trackedTransaction.Key.IsPruned)
				{
					_CoinsData = trackedTransaction.ReceivedCoins.Select(c => new CoinData(c.Outpoint.N, c.TxOut)).ToArray();
				}
			}
			public TrackedTransactionKey Key { get; }
			Transaction _Transaction;
			public Transaction Transaction
			{
				get
				{
					return _Transaction;
				}
				set
				{
					_Transaction = value;
				}
			}


			CoinData[] _CoinsData;
			CoinData[] CoinsData
			{
				get
				{
					return _CoinsData;
				}
				set
				{
					_CoinsData = value;
				}
			}


			long _TickCount;
			public long TickCount
			{
				get
				{
					return _TickCount;
				}
				set
				{
					_TickCount = value;
				}
			}

			public Dictionary<Script, KeyPath> KnownKeyPathMapping { get; set; }

			long _FirstSeenTickCount;
			public long FirstSeenTickCount
			{
				get
				{
					return _FirstSeenTickCount;
				}
				set
				{
					_FirstSeenTickCount = value;
				}
			}

			public bool NeedUpdate
			{
				get; set;
			}
			public bool NeedRemove { get; internal set; }

			public void ReadWrite(BitcoinStream stream)
			{
				if (Key.IsPruned)
				{
					stream.ReadWrite(ref _CoinsData);
				}
				else
				{
					stream.ReadWrite(ref _Transaction);
				}
				if (stream.Serializing || stream.Inner.Position != stream.Inner.Length)
				{
					stream.ReadWrite(ref _TickCount);
					// We always with FirstSeenTickCount to be at least TickCount
					if (!stream.Serializing)
						_FirstSeenTickCount = _TickCount;
				}
				else
				{
					NeedUpdate = true;
				}
				if (stream.Serializing || stream.Inner.Position != stream.Inner.Length)
				{
					if (stream.Serializing)
					{
						var match = new TransactionMiniMatch();
						match.Outputs = KnownKeyPathMapping.Select(kv => new TransactionMiniKeyInformation() { ScriptPubKey = kv.Key, KeyPath = kv.Value }).ToArray();
						stream.ReadWrite(ref match);
					}
					else
					{
						var match = new TransactionMiniMatch();
						stream.ReadWrite(ref match);
						KnownKeyPathMapping = new Dictionary<Script, KeyPath>();
						foreach (var kv in match.Inputs.Concat(match.Outputs))
						{
							KnownKeyPathMapping.TryAdd(kv.ScriptPubKey, kv.KeyPath);
						}
					}
				}
				else
				{
					NeedUpdate = true;
				}
				if (stream.Serializing || stream.Inner.Position != stream.Inner.Length)
				{
					stream.ReadWrite(ref _FirstSeenTickCount);
				}
				else
				{
					NeedUpdate = true;
				}
			}

			internal string GetRowKey()
			{
				return Key.ToString();
			}

			public TrackedTransaction ToTrackedTransaction(TrackedSource trackedSource)
			{
				var trackedTransaction = Key.IsPruned
										? new TrackedTransaction(Key, trackedSource, GetCoins(), KnownKeyPathMapping)
										: new TrackedTransaction(Key, trackedSource, Transaction, KnownKeyPathMapping);
				trackedTransaction.Inserted = TickCount == 0 ? NBitcoin.Utils.UnixTimeToDateTime(0) : new DateTimeOffset((long)TickCount, TimeSpan.Zero);
				trackedTransaction.FirstSeen = FirstSeenTickCount == 0 ? NBitcoin.Utils.UnixTimeToDateTime(0) : new DateTimeOffset((long)FirstSeenTickCount, TimeSpan.Zero);
				return trackedTransaction;
			}

			private IEnumerable<Coin> GetCoins()
			{
				foreach (var coinData in _CoinsData)
				{
					yield return new Coin(new OutPoint(Key.TxId, (int)coinData.Index), coinData.TxOut);
				}
			}
		}

		public async Task SaveMetadata<TMetadata>(TrackedSource source, string key, TMetadata value) where TMetadata : class
		{
			await _TxContext.DoAsync(tx =>
			{
				var table = GetMetadataIndex(tx, source);
				if (value != null)
					table.Insert(key, Zip(Serializer.ToString(value)));
				else
					table.RemoveKey(key);
				tx.Commit();
			});
		}
		public async Task<TMetadata> GetMetadata<TMetadata>(TrackedSource source, string key) where TMetadata: class
		{
			return await _TxContext.DoAsync(tx =>
			{
				var table = GetMetadataIndex(tx, source);
				foreach (var row in table.SelectForwardSkip(0, key))
				{
					return Serializer.ToObject<TMetadata>(Unzip(row.Value));
				}
				return null;
			});
		}

		public async Task SaveMatches(TrackedTransaction[] transactions)
		{
			if (transactions.Length == 0)
				return;
			var groups = transactions.GroupBy(i => i.TrackedSource);

			await _TxContext.DoAsync(tx =>
			{
				foreach (var group in groups)
				{
					var table = GetTransactionsIndex(tx, group.Key);

					foreach (var value in group)
					{
						if (group.Key is DerivationSchemeTrackedSource s)
						{
							foreach (var kv in value.KnownKeyPathMapping)
							{
								var info = new KeyPathInformation(keyPathTemplates, kv.Value, s.DerivationStrategy, Network);
								var availableIndex = GetAvailableKeysIndex(tx, s.DerivationStrategy, info.Feature);
								var reservedIndex = GetReservedKeysIndex(tx, s.DerivationStrategy, info.Feature);
								var index = info.GetIndex();
								var bytes = availableIndex.SelectBytes(index);
								if (bytes != null)
								{
									availableIndex.RemoveKey(index);
								}
								bytes = reservedIndex.SelectBytes(index);
								if (bytes != null)
								{
									reservedIndex.RemoveKey(index);
								}
							}
						}
						var ms = new MemoryStream();
						BitcoinStream bs = new BitcoinStream(ms, true);
						bs.ConsensusFactory = Network.NBitcoinNetwork.Consensus.ConsensusFactory;
						TransactionMatchData data = new TransactionMatchData(value);
						bs.ReadWrite(data);
						table.Insert(data.GetRowKey(), ms.ToArrayEfficient());
					}
				}
				tx.Commit();
			});
		}

		internal Task Prune(TrackedSource trackedSource, List<TrackedTransaction> prunable)
		{
			if (prunable == null || prunable.Count == 0)
				return Task.CompletedTask;
			return _TxContext.DoAsync(tx =>
			{
				var table = GetTransactionsIndex(tx, trackedSource);
				foreach (var tracked in prunable)
				{
					table.RemoveKey(tracked.Key.ToString());
					if (tracked.Key.BlockHash != null)
					{
						var pruned = tracked.Prune();
						var data = new TransactionMatchData(pruned);
						MemoryStream ms = new MemoryStream();
						BitcoinStream bs = new BitcoinStream(ms, true);
						bs.ConsensusFactory = Network.NBitcoinNetwork.Consensus.ConsensusFactory;
						data.ReadWrite(bs);
						table.Insert(data.GetRowKey(), ms.ToArrayEfficient());
					}
				}
				tx.Commit();
			});
		}

		public async Task CleanTransactions(TrackedSource trackedSource, List<TrackedTransaction> cleanList)
		{
			if (cleanList == null || cleanList.Count == 0)
				return;
			await _TxContext.DoAsync(tx =>
			{
				var table = GetTransactionsIndex(tx, trackedSource);
				foreach (var tracked in cleanList)
				{
					table.RemoveKey(tracked.Key.ToString());
				}
				tx.Commit();
			});
		}

		internal Task UpdateAddressPool(DerivationSchemeTrackedSource trackedSource, Dictionary<DerivationFeature, int?> highestKeyIndexFound)
		{
			return _TxContext.DoAsync(tx =>
			{
				tx.ValuesLazyLoadingIsOn = false;
				foreach (var kv in highestKeyIndexFound)
				{
					if (kv.Value == null)
						continue;
					var index = GetAvailableKeysIndex(tx, trackedSource.DerivationStrategy, kv.Key);
					bool needRefill = CleanUsed(kv.Value.Value, index);
					index = GetReservedKeysIndex(tx, trackedSource.DerivationStrategy, kv.Key);
					needRefill |= CleanUsed(kv.Value.Value, index);
					if (needRefill)
					{
						var hIndex = GetHighestPathIndex(tx, trackedSource.DerivationStrategy, kv.Key);
						int highestGenerated = hIndex.SelectInt(0) ?? -1;
						if (highestGenerated < kv.Value.Value)
							hIndex.Insert(0, kv.Value.Value);
						var toGenerate = GetAddressToGenerateCount(tx, trackedSource.DerivationStrategy, kv.Key);
						RefillAvailable(tx, trackedSource.DerivationStrategy, kv.Key, toGenerate);
					}
				}
				tx.Commit();
			});
		}

		private bool CleanUsed(int highestIndex, Index index)
		{
			bool needRefill = false;
			foreach (var row in index.SelectForwardSkip(0))
			{
				var keyInfo = ToObject<KeyPathInformation>(row.Value);
				if (keyInfo.GetIndex() <= highestIndex)
				{
					index.RemoveKey(keyInfo.GetIndex());
					needRefill = true;
				}
			}
			return needRefill;
		}

		FixedSizeCache<uint256, uint256> noMatchCache = new FixedSizeCache<uint256, uint256>(5000, k => k);
		public Task<TrackedTransaction[]> GetMatches(Transaction tx, uint256 blockId, DateTimeOffset now, bool useCache)
		{
			return GetMatches(new[] { tx }, blockId, now, useCache);
		}
		public async Task<TrackedTransaction[]> GetMatches(IList<Transaction> txs, uint256 blockId, DateTimeOffset now, bool useCache)
		{
			foreach (var tx in txs)
				tx.PrecomputeHash(false, true);
			var transactionsPerScript = new MultiValueDictionary<Script, Transaction>();
			var matches = new Dictionary<string, TrackedTransaction>();
			HashSet<Script> scripts = new HashSet<Script>(txs.Count);
			var noMatchTransactions = new HashSet<uint256>(txs.Count);
			foreach (var tx in txs)
			{
				if (blockId != null && useCache && noMatchCache.Contains(tx.GetHash()))
				{
					continue;
				}
				noMatchTransactions.Add(tx.GetHash());
				foreach (var input in tx.Inputs)
				{
					var signer = input.GetSigner();
					if (signer != null)
					{
						scripts.Add(signer.ScriptPubKey);
						transactionsPerScript.Add(signer.ScriptPubKey, tx);
					}
				}
				foreach (var output in tx.Outputs)
				{
					scripts.Add(output.ScriptPubKey);
					transactionsPerScript.Add(output.ScriptPubKey, tx);
				}
			}
			if (scripts.Count == 0)
				return Array.Empty<TrackedTransaction>();
			var keyInformations = await GetKeyInformations(scripts.ToArray());
			foreach (var keyInfoByScripts in keyInformations)
			{
				foreach (var tx in transactionsPerScript[keyInfoByScripts.Key])
				{
					if (keyInfoByScripts.Value.Count != 0)
						noMatchTransactions.Remove(tx.GetHash());
					foreach (var keyInfo in keyInfoByScripts.Value)
					{
						var matchesGroupingKey = $"{keyInfo.DerivationStrategy?.ToString() ?? keyInfo.ScriptPubKey.ToHex()}-[{tx.GetHash()}]";
						if (!matches.TryGetValue(matchesGroupingKey, out TrackedTransaction match))
						{
							match = new TrackedTransaction(
								new TrackedTransactionKey(tx.GetHash(), blockId, false),
								keyInfo.TrackedSource,
								tx,
								new Dictionary<Script, KeyPath>())
							{
								FirstSeen = now,
								Inserted = now
							};
							matches.Add(matchesGroupingKey, match);
						}
						if (keyInfo.KeyPath != null)
							match.KnownKeyPathMapping.TryAdd(keyInfo.ScriptPubKey, keyInfo.KeyPath);
					}
				}
			}
			foreach (var m in matches.Values)
			{
				m.KnownKeyPathMappingUpdated();
			}

			foreach (var tx in txs)
			{
				if (blockId == null &&
					noMatchTransactions.Contains(tx.GetHash()))
				{
					noMatchCache.Add(tx.GetHash());
				}
			}
			return matches.Values.Count == 0 ? Array.Empty<TrackedTransaction>() : matches.Values.ToArray();
		}
	}
}