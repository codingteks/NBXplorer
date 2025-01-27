﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace NBXplorer.Models
{
	public class GenerateWalletRequest
	{
		public int AccountNumber { get; set; }
		[JsonConverter(typeof(NBXplorer.JsonConverters.WordlistJsonConverter))]
		public NBitcoin.Wordlist WordList { get; set; }
		[JsonConverter(typeof(NBXplorer.JsonConverters.WordcountJsonConverter))]
		public NBitcoin.WordCount? WordCount { get; set; }
		[JsonConverter(typeof(NBXplorer.JsonConverters.ScriptPubKeyTypeConverter))]
		public NBitcoin.ScriptPubKeyType? ScriptPubKeyType { get; set; }
		public string Passphrase { get; set; }
		public bool ImportKeysToRPC { get; set; }
	}
}
