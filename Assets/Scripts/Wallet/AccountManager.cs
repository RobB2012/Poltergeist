﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;

using Phantasma.Cryptography;
using Phantasma.Storage;
using Phantasma.Numerics;
using System;
using System.Linq;
using Phantasma.SDK;
using Phantasma.Neo.Core;
using Phantasma.Domain;
using Phantasma.Core;
using Phantasma.Core.Utils;
using Phantasma.Core.Types;
using Phantasma.Ethereum;
using LunarLabs.Parser;
using Phantasma.VM.Utils;
using Phantasma.Blockchain;
using Archive = Phantasma.SDK.Archive;

namespace Poltergeist
{
    public enum WalletState
    {
        Refreshing,
        Ready,
        Error
    }

    [Flags]
    public enum PlatformKind
    {
        None = 0x0,
        Phantasma = 0x1,
        Neo = 0x2,
        Ethereum = 0x4,
    }

    public struct Account
    {
        public string name;
        public PlatformKind platforms;
        public string phaAddress;
        public string neoAddress;
        public string ethAddress;
        public string WIF;
        public bool passwordProtected;
        public int passwordIterations;
        public string salt;
        public string iv;
        public string password; // Not used after account upgrade to version 2.
        public string misc;

        public override string ToString()
        {
            return $"{name.ToUpper()} [{platforms}]";
        }

        public string GetWif(string passwordHash)
        {
            return String.IsNullOrEmpty(passwordHash) ? WIF : AccountManager.DecryptString(WIF, passwordHash, iv);
        }
    }

    public struct AccountLegacyV1
    {
        public static readonly int MinPasswordLength = 6;
        public static readonly int MaxPasswordLength = 32;

        public string name;
        public PlatformKind platforms;
        public string WIF;
        public string password;
        public string misc;

        public override string ToString()
        {
            return $"{name.ToUpper()} [{platforms}]";
        }
    }

    public struct AccountsExport
    {
        public string walletIdentifier;
        public int accountsVersion;
        public string accounts;
        public bool passwordProtected;
        public int passwordIterations;
        public string salt;
        public string iv;
    }

    public struct HistoryEntry
    {
        public string hash;
        public DateTime date;
        public string url;
    }

    public enum AccountFlags
    {
        None = 0x0,
        Master = 0x1,
        Validator = 0x2
    }

    public static class AccountFlagsExtensions
    {
        public static List<PlatformKind> Split(this PlatformKind kind)
        {
            var list = new List<PlatformKind>();
            foreach (var platform in AccountManager.AvailablePlatforms)
            {
                if (kind.HasFlag(platform))
                {
                    list.Add(platform);
                }
            }
            return list;
        }

        public static PlatformKind GetTransferTargets(this PlatformKind kind, Token token)
        {
            if (!token.flags.Contains("Foreign"))
            {
                return kind;
            }

            PlatformKind targets;

            switch (kind)
            {
                case PlatformKind.Phantasma:
                    targets = PlatformKind.Phantasma;
                    targets |= AccountManager.SupportedTokens.Any(x => x.symbol == token.symbol && x.platform.ToUpper() == PlatformKind.Neo.ToString().ToUpper()) ? PlatformKind.Neo : PlatformKind.None;
                    targets |= AccountManager.SupportedTokens.Any(x => x.symbol == token.symbol && x.platform.ToUpper() == PlatformKind.Ethereum.ToString().ToUpper()) ? PlatformKind.Ethereum : PlatformKind.None;
                    return targets;

                case PlatformKind.Neo:
                    targets = PlatformKind.Neo;
                    targets |= AccountManager.SupportedTokens.Any(x => x.symbol == token.symbol && x.platform.ToUpper() == PlatformKind.Phantasma.ToString().ToUpper()) ? PlatformKind.Phantasma : PlatformKind.None;
                    return targets;

                case PlatformKind.Ethereum:
                    targets = PlatformKind.Ethereum;
                    targets |= AccountManager.SupportedTokens.Any(x => x.symbol == token.symbol && x.platform.ToUpper() == PlatformKind.Phantasma.ToString().ToUpper()) ? PlatformKind.Phantasma : PlatformKind.None;
                    return targets;

                default:
                    return PlatformKind.None;
            }
        }
        public static bool ValidateTransferTarget(this PlatformKind kind, Token token, PlatformKind targetKind)
        {
            var targets = kind.GetTransferTargets(token);
            return targets.HasFlag(targetKind);
        }
    }

    public struct TransferRequest
    {
        public PlatformKind platform;
        public string key;
        public string destination;
        public string symbol;
        public decimal amount;
        public string interop;
    }

    public class AccountState
    {
        public PlatformKind platform;
        public string name;
        public string address;
        public Balance[] balances;
        public AccountFlags flags;
        public Timestamp stakeTime;

        public Archive[] archives;
        public string avatarData;
        public uint availableStorage;
        public uint usedStorage;
        public uint totalStorage => availableStorage + usedStorage;

        public Dictionary<string, string> dappTokens = new Dictionary<string, string>();

        public decimal GetAvailableAmount(string symbol)
        {
            for (int i = 0; i < balances.Length; i++)
            {
                var entry = balances[i];
                if (entry.Symbol == symbol)
                {
                    return entry.Available;
                }
            }

            return 0;
        }

        public void RegisterDappToken(string dapp, string token)
        {
            dappTokens[dapp] = token;
        }
    }

    public class Balance
    {
        public string Symbol;
        public decimal Available;
        public decimal Staked;
        public decimal Pending;
        public decimal Claimable;
        public string Chain;
        public int Decimals;
        public string PendingPlatform;
        public string PendingHash;
        public string[] Ids;

        public decimal Total => Available + Staked + Pending + Claimable;
    }

    public class AccountManager : MonoBehaviour
    {
        public static readonly int MinPasswordLength = 6;
        public static readonly int MaxPasswordLength = 32;
        public static readonly int MinAccountNameLength = 3;
        public static readonly int MaxAccountNameLength = 16;
        public string WalletIdentifier => "PGT" + UnityEngine.Application.version;

        public Settings Settings { get; private set; }

        public List<Account> Accounts { get; private set; }
        public bool AccountsAreReadyToBeUsed = false;

        public static List<Token> SupportedTokens = null;
        private Dictionary<string, decimal> _tokenPrices = new Dictionary<string, decimal>();
        public string CurrentTokenCurrency { get; private set; }

        private int _selectedAccountIndex;
        public int CurrentIndex => _selectedAccountIndex;
        public Account CurrentAccount => HasSelection ? Accounts[_selectedAccountIndex] : new Account() { };
        public string CurrentPasswordHash;
        public string CurrentWif => Accounts[_selectedAccountIndex].GetWif(CurrentPasswordHash);

        public bool HasSelection => _selectedAccountIndex >= 0 && _selectedAccountIndex < Accounts.Count();

        private Dictionary<PlatformKind, AccountState> _states = new Dictionary<PlatformKind, AccountState>();
        private Dictionary<PlatformKind, List<TokenData>> _nfts = new Dictionary<PlatformKind, List<TokenData>>();
        private Dictionary<PlatformKind, HistoryEntry[]> _history = new Dictionary<PlatformKind, HistoryEntry[]>();

        public PlatformKind CurrentPlatform { get; set; }
        public AccountState CurrentState => _states.ContainsKey(CurrentPlatform) ? _states[CurrentPlatform] : null;
        public List<TokenData> CurrentNfts => _nfts.ContainsKey(CurrentPlatform) ? _nfts[CurrentPlatform] : null;
        public HistoryEntry[] CurrentHistory => _history.ContainsKey(CurrentPlatform) ? _history[CurrentPlatform] : null;

        private bool nftDescriptionsAreFullyLoaded;
        private TtrsNftSortMode currentTtrsNftsSortMode = TtrsNftSortMode.None;
        private NftSortMode currentNftsSortMode = NftSortMode.None;
        private SortDirection currentNftsSortDirection = SortDirection.None;

        public static AccountManager Instance { get; private set; }

        public string Status { get; private set; }
        public bool Ready => Status == "ok";
        public bool Refreshing => _pendingRequestCount > 0;

        public Phantasma.SDK.PhantasmaAPI phantasmaApi { get; private set; }
        public Phantasma.SDK.EthereumAPI ethereumApi { get; private set; }
        public Phantasma.Neo.Core.NeoAPI neoApi;

        public static PlatformKind[] AvailablePlatforms { get; private set; }

        private Dictionary<string, string> _currencyMap = new Dictionary<string, string>();
        public IEnumerable<string> Currencies => _currencyMap.Keys;

        public static readonly int SoulMasterStakeAmount = 50000;

        private DateTime _lastPriceUpdate = DateTime.MinValue;

        private int _pendingRequestCount;

        private bool _accountInitialized;

        private string etherscanAPIToken;

        private void Awake()
        {
            Instance = this;
            Settings = new Settings();

            Status = "Initializing wallet...";

            _currencyMap["AUD"] = "A$";
            _currencyMap["CAD"] = "C$";
            _currencyMap["EUR"] = "€";
            _currencyMap["GBP"] = "\u00A3";
            _currencyMap["RUB"] = "\u20BD";
            _currencyMap["USD"] = "$";
            _currencyMap["JPY"] = "¥";

            var ethereumAPIKeys = Resources.Load<TextAsset>("ethereum_api");
            if (ethereumAPIKeys != null)
            {
                var lines = ethereumAPIKeys.text.Split('\n');
                if (lines.Length > 0)
                {
                    etherscanAPIToken = lines[0].Trim();
                }
            }
            if (string.IsNullOrEmpty(etherscanAPIToken))
            {
                Log.WriteWarning("No Etherscan API key found, Ethereum balances wont work!");
            }

            var platforms = new List<PlatformKind>();
            platforms.Add(PlatformKind.Phantasma);
            platforms.Add(PlatformKind.Neo);
            platforms.Add(PlatformKind.Ethereum);

            AvailablePlatforms = platforms.ToArray();
        }

        public string GetTokenWorth(string symbol, decimal amount)
        {
            bool hasLocalCurrency = !string.IsNullOrEmpty(CurrentTokenCurrency) && _currencyMap.ContainsKey(CurrentTokenCurrency);
            if (_tokenPrices.ContainsKey(symbol) && hasLocalCurrency)
            {
                var price = _tokenPrices[symbol] * amount;
                var ch = _currencyMap[CurrentTokenCurrency];
                return $"{WalletGUI.MoneyFormat(price, WalletGUI.MoneyFormatType.Short)} {ch}";
            }
            else
            {
                return "-";
            }
        }

        private IEnumerator FetchTokenPrices(IEnumerable<Token> symbols, string currency)
        {
            var separator = "%2C";
            var url = "https://api.coingecko.com/api/v3/simple/price?ids=" + string.Join(separator, symbols.Where(x => !String.IsNullOrEmpty(x.apiSymbol)).Select(x => x.apiSymbol).Distinct().ToList()) + "&vs_currencies=" + currency;
            return WebClient.RESTRequest(url, WebClient.DefaultTimeout, (error, msg) =>
            {

            },
            (response) =>
            {
                try
                {
                    foreach (var symbol in symbols)
                    {
                        var node = response.GetNode(symbol.apiSymbol);
                        if (node != null)
                        {
                            var price = node.GetDecimal(currency);

                            SetTokenPrice(symbol.symbol, price);
                        }
                        else
                        {
                            Log.Write($"Cannot get price for '{symbol.apiSymbol}'.");
                        }
                    }

                    // GOATI token price is pegged to 0.1$.
                    SetTokenPrice("GOATI", Convert.ToDecimal(0.1));
                }
                catch (Exception e)
                {
                    Log.WriteWarning(e.ToString());
                }
            });
        }

        private void SetTokenPrice(string symbol, decimal price)
        {
            Log.Write($"Got price for {symbol} => {price}");
            _tokenPrices[symbol] = price;
        }

        public const string WalletVersionTag = "wallet.list.version";
        public const string WalletTag = "wallet.list";
        // TODO: Remove before release.
        public const string WalletLegacyTag = "wallet.list.legacy";

        private int rpcNumberPhantasma; // Total number of Phantasma RPCs, received from getpeers.json.
        private int rpcNumberNeo; // Total number of Neo RPCs.
        private int rpcBenchmarkedPhantasma; // Number of Phantasma RPCs which speed already measured.
        private int rpcBenchmarkedNeo; // Number of Neo RPCs which speed already measured.
        public int rpcAvailablePhantasma = 0;
        public int rpcAvailableNeo = 0;
        private class RpcBenchmarkData
        {
            public string Url;
            public bool ConnectionError;
            public TimeSpan ResponseTime;

            public RpcBenchmarkData(string url, bool connectionError, TimeSpan responseTime)
            {
                Url = url;
                ConnectionError = connectionError;
                ResponseTime = responseTime;
            }
        }
        private List<RpcBenchmarkData> rpcResponseTimesPhantasma;
        private List<RpcBenchmarkData> rpcResponseTimesNeo;

        private string GetFastestWorkingRPCURL(PlatformKind platformKind, out TimeSpan responseTime)
        {
            string fastestRpcUrl = null;

            List<RpcBenchmarkData> platformRpcs = null;
            if (platformKind == PlatformKind.Phantasma)
                platformRpcs = rpcResponseTimesPhantasma;
            else if (platformKind == PlatformKind.Neo)
                platformRpcs = rpcResponseTimesNeo;

            foreach (var rpcResponseTime in platformRpcs)
            {
                if (!rpcResponseTime.ConnectionError && String.IsNullOrEmpty(fastestRpcUrl))
                {
                    // At first just initializing with first working RPC.
                    fastestRpcUrl = rpcResponseTime.Url;
                    responseTime = rpcResponseTime.ResponseTime;
                }
                else if (!rpcResponseTime.ConnectionError && rpcResponseTime.ResponseTime < responseTime)
                {
                    // Faster RPC found, switching.
                    fastestRpcUrl = rpcResponseTime.Url;
                    responseTime = rpcResponseTime.ResponseTime;
                }
            }
            return fastestRpcUrl;
        }

        public void UpdateRPCURL(PlatformKind platformKind)
        {
            if (Settings.nexusKind != NexusKind.Main_Net)
            {
                rpcAvailablePhantasma = 1;
                rpcAvailableNeo = 1;
                return; // No need to change RPC, it is set by custom settings.
            }

            if (platformKind == PlatformKind.Phantasma)
            {
                var url = $"https://ghostdevs.com/getpeers.json";

                rpcBenchmarkedPhantasma = 0;
                rpcResponseTimesPhantasma = new List<RpcBenchmarkData>();

                StartCoroutine(
                    WebClient.RESTRequest(url, WebClient.DefaultTimeout, (error, msg) =>
                    {
                        Log.Write("auto error => " + error);
                    },
                    (response) =>
                    {
                        if (response != null)
                        {
                            rpcNumberPhantasma = response.ChildCount;

                            if (String.IsNullOrEmpty(Settings.phantasmaRPCURL))
                            {
                                // Checking if we are still on mainnet
                                if (Settings.nexusKind == NexusKind.Main_Net)
                                {
                                    // If we have no previously used RPC, we select random one at first.
                                    var index = ((int)(Time.realtimeSinceStartup * 1000)) % rpcNumberPhantasma;
                                    var node = response.GetNodeByIndex(index);
                                    var result = node.GetString("url") + "/rpc";
                                    Settings.phantasmaRPCURL = result;
                                    Log.Write($"Changed Phantasma RPC url {index} => {result}");
                                }
                            }

                            UpdateAPIs();

                            // Benchmarking RPCs.
                            foreach (var node in response.Children)
                            {
                                var rpcUrl = node.GetString("url") + "/rpc";

                                StartCoroutine(
                                    WebClient.Ping(rpcUrl, (error, msg) =>
                                    {
                                        Log.Write("Ping error: " + error);

                                        rpcBenchmarkedPhantasma++;

                                        lock (rpcResponseTimesPhantasma)
                                        {
                                            rpcResponseTimesPhantasma.Add(new RpcBenchmarkData(rpcUrl, true, new TimeSpan()));
                                        }

                                        if (rpcBenchmarkedPhantasma == rpcNumberPhantasma)
                                        {
                                        // We finished benchmarking, time to select best RPC server.
                                        TimeSpan bestTime;
                                            string bestRpcUrl = GetFastestWorkingRPCURL(PlatformKind.Phantasma, out bestTime);

                                            if (String.IsNullOrEmpty(bestRpcUrl))
                                            {
                                                Log.WriteWarning("All Phantasma RPC servers are unavailable. Please check your network connection.");
                                            }
                                            else
                                            {
                                                Log.Write($"Fastest Phantasma RPC is {bestRpcUrl}: {new DateTime(bestTime.Ticks).ToString("ss.fff")} sec.");

                                            // Checking if we are still on mainnet
                                            if (Settings.nexusKind == NexusKind.Main_Net)
                                                {
                                                    Settings.phantasmaRPCURL = bestRpcUrl;
                                                    UpdateAPIs();
                                                    Settings.SaveOnExit();
                                                }
                                            }
                                        }
                                    },
                                    (responseTime) =>
                                    {
                                        rpcBenchmarkedPhantasma++;

                                        rpcAvailablePhantasma++;

                                        lock (rpcResponseTimesPhantasma)
                                        {
                                            rpcResponseTimesPhantasma.Add(new RpcBenchmarkData(rpcUrl, false, responseTime));
                                        }

                                        if (rpcBenchmarkedPhantasma == rpcNumberPhantasma)
                                        {
                                        // We finished benchmarking, time to select best RPC server.
                                        TimeSpan bestTime;
                                            string bestRpcUrl = GetFastestWorkingRPCURL(PlatformKind.Phantasma, out bestTime);

                                            if (String.IsNullOrEmpty(bestRpcUrl))
                                            {
                                                Log.WriteWarning("All Phantasma RPC servers are unavailable. Please check your network connection.");
                                            }
                                            else
                                            {
                                            // Checking if we are still on mainnet
                                            if (Settings.nexusKind == NexusKind.Main_Net)
                                                {
                                                    Log.Write($"Fastest Phantasma RPC is {bestRpcUrl}: {new DateTime(bestTime.Ticks).ToString("ss.fff")} sec.");
                                                    Settings.phantasmaRPCURL = bestRpcUrl;
                                                    UpdateAPIs();
                                                    Settings.SaveOnExit();
                                                }
                                            }
                                        }
                                    })
                                );
                            }
                        }
                    })
                );
            }
            else if (platformKind == PlatformKind.Neo)
            {
                rpcBenchmarkedNeo = 0;
                rpcResponseTimesNeo = new List<RpcBenchmarkData>();

                var neoRpcList = Phantasma.Neo.Utils.NeoRpcs.GetList();
                rpcNumberNeo = neoRpcList.Count;

                if (String.IsNullOrEmpty(Settings.neoRPCURL))
                {
                    // Checking if we are still on mainnet
                    if (Settings.nexusKind == NexusKind.Main_Net)
                    {
                        // If we have no previously used RPC, we select random one at first.
                        var index = ((int)(Time.realtimeSinceStartup * 1000)) % rpcNumberNeo;
                        var result = neoRpcList[index];
                        Settings.neoRPCURL = result;
                        Log.Write($"Changed Neo RPC url {index} => {result}");
                    }
                }

                UpdateAPIs();

                // Benchmarking RPCs.
                foreach (var rpcUrl in neoRpcList)
                {
                    StartCoroutine(
                        WebClient.Ping(rpcUrl, (error, msg) =>
                        {
                            Log.Write("Ping error: " + error);

                            rpcBenchmarkedNeo++;

                            lock (rpcResponseTimesNeo)
                            {
                                rpcResponseTimesNeo.Add(new RpcBenchmarkData(rpcUrl, true, new TimeSpan()));
                            }

                            if (rpcBenchmarkedNeo == rpcNumberNeo)
                            {
                                // We finished benchmarking, time to select best RPC server.
                                TimeSpan bestTime;
                                string bestRpcUrl = GetFastestWorkingRPCURL(PlatformKind.Neo, out bestTime);

                                if (String.IsNullOrEmpty(bestRpcUrl))
                                {
                                    Log.WriteWarning("All Neo RPC servers are unavailable. Please check your network connection.");
                                }
                                else
                                {
                                    // Checking if we are still on mainnet
                                    if (Settings.nexusKind == NexusKind.Main_Net)
                                    {
                                        Log.Write($"Fastest Neo RPC is {bestRpcUrl}: {new DateTime(bestTime.Ticks).ToString("ss.fff")} sec.");
                                        Settings.neoRPCURL = bestRpcUrl;
                                        UpdateAPIs();
                                        Settings.SaveOnExit();
                                    }
                                }
                            }
                        },
                        (responseTime) =>
                        {
                            rpcBenchmarkedNeo++;

                            rpcAvailableNeo++;

                            lock (rpcResponseTimesNeo)
                            {
                                rpcResponseTimesNeo.Add(new RpcBenchmarkData(rpcUrl, false, responseTime));
                            }

                            if (rpcBenchmarkedNeo == rpcNumberNeo)
                            {
                                // We finished benchmarking, time to select best RPC server.
                                TimeSpan bestTime;
                                string bestRpcUrl = GetFastestWorkingRPCURL(PlatformKind.Neo, out bestTime);

                                if (String.IsNullOrEmpty(bestRpcUrl))
                                {
                                    Log.WriteWarning("All Neo RPC servers are unavailable. Please check your network connection.");
                                }
                                else
                                {
                                    // Checking if we are still on mainnet
                                    if (Settings.nexusKind == NexusKind.Main_Net)
                                    {
                                        Log.Write($"Fastest Neo RPC is {bestRpcUrl}: {new DateTime(bestTime.Ticks).ToString("ss.fff")} sec.");
                                        Settings.neoRPCURL = bestRpcUrl;
                                        UpdateAPIs();
                                        Settings.SaveOnExit();
                                    }
                                }
                            }
                        })
                    );
                }
            }
        }

        public void ChangeFaultyRPCURL(PlatformKind platformKind = PlatformKind.Phantasma)
        {
            if (Settings.nexusKind != NexusKind.Main_Net)
                return; // Fallback works only for mainnet.

            if (platformKind == PlatformKind.Phantasma)
            {
                Log.Write($"Changing faulty Phantasma RPC {Settings.phantasmaRPCURL}.");

                // Now we have one less working RPC.
                if(rpcAvailablePhantasma > 0)
                    rpcAvailablePhantasma--;

                // Marking faulty RPC.
                var currentRpc = rpcResponseTimesPhantasma.Find(x => x.Url == Settings.phantasmaRPCURL);
                if (currentRpc != null)
                    currentRpc.ConnectionError = true;

                // Switching to working RPC.
                TimeSpan bestTime;
                string bestRpcUrl = GetFastestWorkingRPCURL(platformKind, out bestTime);

                if (String.IsNullOrEmpty(bestRpcUrl))
                {
                    Log.WriteWarning("All Phantasma RPC servers are unavailable. Please check your network connection.");
                }
                else
                {
                    Log.Write($"Next fastest Phantasma RPC is {bestRpcUrl}: {new DateTime(bestTime.Ticks).ToString("ss.fff")} sec.");
                    Settings.phantasmaRPCURL = bestRpcUrl;
                    UpdateAPIs();
                }
            }
            else if (platformKind == PlatformKind.Neo)
            {
                // TODO: This code is not used yet, ChangeFaultyRPCURL() not called on Neo connection errors.

                Log.Write($"Changing faulty Neo RPC {Settings.neoRPCURL}.");

                // Now we have one less working RPC.
                if (rpcAvailableNeo > 0)
                    rpcAvailableNeo--;

                // Marking faulty RPC.
                var currentRpc = rpcResponseTimesNeo.Find(x => x.Url == Settings.neoRPCURL);
                if (currentRpc != null)
                    currentRpc.ConnectionError = true;

                // Switching to working RPC.
                TimeSpan bestTime;
                string bestRpcUrl = GetFastestWorkingRPCURL(platformKind, out bestTime);

                if (String.IsNullOrEmpty(bestRpcUrl))
                {
                    Log.WriteWarning("All Neo RPC servers are unavailable. Please check your network connection.");
                }
                else
                {
                    Log.Write($"Next fastest Neo RPC is {bestRpcUrl}: {new DateTime(bestTime.Ticks).ToString("ss.fff")} sec.");
                    Settings.neoRPCURL = bestRpcUrl;
                    UpdateAPIs();
                }
            }
        }
        public static readonly int PasswordIterations = 100000;
        private static readonly int PasswordSaltByteSize = 64;
        private static readonly int PasswordHashByteSize = 32;
        public static void GetPasswordHash(string password, int passwordIterations, out string salt, out string passwordHash)
        {
            BouncyCastleHashing hashing = new BouncyCastleHashing();
            salt = Convert.ToBase64String(hashing.CreateSalt(PasswordSaltByteSize));
            passwordHash = hashing.PBKDF2_SHA256_GetHash(password, salt, passwordIterations, PasswordHashByteSize);
        }
        public static void GetPasswordHashBySalt(string password, int passwordIterations, string salt, out string passwordHash)
        {
            BouncyCastleHashing hashing = new BouncyCastleHashing();
            passwordHash = hashing.PBKDF2_SHA256_GetHash(password, salt, passwordIterations, PasswordHashByteSize);
        }
        public static string EncryptString(string stringToEncrypt, string key, out string iv)
        {
            var ivBytes = new byte[16];

            //Set up
            var keyParam = new Org.BouncyCastle.Crypto.Parameters.KeyParameter(Convert.FromBase64String(key));

            var secRandom = new Org.BouncyCastle.Security.SecureRandom();
            secRandom.NextBytes(ivBytes);

            var keyParamWithIV = new Org.BouncyCastle.Crypto.Parameters.ParametersWithIV(keyParam, ivBytes, 0, 16);

            var engine = new Org.BouncyCastle.Crypto.Engines.AesEngine();
            var blockCipher = new Org.BouncyCastle.Crypto.Modes.CbcBlockCipher(engine); //CBC
            var cipher = new Org.BouncyCastle.Crypto.Paddings.PaddedBufferedBlockCipher(blockCipher); //Default scheme is PKCS5/PKCS7

            // Encrypt
            cipher.Init(true, keyParamWithIV);
            var inputBytes = System.Text.Encoding.UTF8.GetBytes(stringToEncrypt);
            var outputBytes = new byte[cipher.GetOutputSize(inputBytes.Length)];
            var length = cipher.ProcessBytes(inputBytes, outputBytes, 0);
            cipher.DoFinal(outputBytes, length); //Do the final block

            iv = Convert.ToBase64String(ivBytes);
            return Convert.ToBase64String(outputBytes);
        }
        public static string DecryptString(string stringToDecrypt, string key, string iv)
        {
            //Set up
            var keyParam = new Org.BouncyCastle.Crypto.Parameters.KeyParameter(Convert.FromBase64String(key));
            var ivBytes = Convert.FromBase64String(iv);
            var keyParamWithIV = new Org.BouncyCastle.Crypto.Parameters.ParametersWithIV(keyParam, ivBytes, 0, 16);

            var engine = new Org.BouncyCastle.Crypto.Engines.AesEngine();
            var blockCipher = new Org.BouncyCastle.Crypto.Modes.CbcBlockCipher(engine); //CBC
            var cipher = new Org.BouncyCastle.Crypto.Paddings.PaddedBufferedBlockCipher(blockCipher);

            cipher.Init(false, keyParamWithIV);
            var inputBytes = Convert.FromBase64String(stringToDecrypt);
            var resultExtraSize = new byte[cipher.GetOutputSize(inputBytes.Length)];
            var length = cipher.ProcessBytes(inputBytes, resultExtraSize, 0);
            length += cipher.DoFinal(resultExtraSize, length); //Do the final block

            var result = new byte[length];
            Array.Copy(resultExtraSize, result, length);

            return System.Text.Encoding.UTF8.GetString(result);
        }

        // Start is called before the first frame update
        void Start()
        {
            Settings.Load();

            UpdateRPCURL(PlatformKind.Phantasma);
            UpdateRPCURL(PlatformKind.Neo);

            LoadNexus();

            // Version 1 - original account version used in PG up to version 1.9.
            // Version 2 - new account version.
            var walletVersion = PlayerPrefs.GetInt(WalletVersionTag, 1);

            var wallets = PlayerPrefs.GetString(WalletTag, "");

            Accounts = new List<Account>();

            if (walletVersion == 1 && !string.IsNullOrEmpty(wallets))
            {
                // TODO: Remove before release.
                // Saving old accounts for now.
                PlayerPrefs.SetString(WalletLegacyTag, wallets);

                // Legacy format, should be converted.
                var bytes = Base16.Decode(wallets);
                var accountsLegacy = Serialization.Unserialize<AccountLegacyV1[]>(bytes);

                foreach (var account in accountsLegacy)
                {
                    Accounts.Add(new Account
                    {
                        name = account.name,
                        platforms = account.platforms,
                        WIF = account.WIF,
                        password = account.password,
                        misc = account.misc
                    });
                }

                // Upgrading accounts.
                for (var i = 0; i < Accounts.Count(); i++)
                {
                    Log.Write($"Account {Accounts[i].name} version: {walletVersion}, will be upgraded");

                    var account = Accounts[i];

                    // Initializing public addresses.
                    var phaKeys = PhantasmaKeys.FromWIF(account.WIF);
                    account.phaAddress = phaKeys.Address.ToString();

                    var neoKeys = NeoKeys.FromWIF(account.WIF);
                    account.neoAddress = neoKeys.Address.ToString();

                    var ethereumAddressUtil = new Phantasma.Ethereum.Util.AddressUtil();
                    account.ethAddress = ethereumAddressUtil.ConvertToChecksumAddress(EthereumKey.FromWIF(account.WIF).Address);

                    if (!String.IsNullOrEmpty(Accounts[i].password))
                    {
                        account.passwordProtected = true;
                        account.passwordIterations = PasswordIterations;

                        // Encrypting WIF.
                        GetPasswordHash(account.password, account.passwordIterations, out string salt, out string passwordHash);
                        account.password = "";
                        account.salt = salt;

                        account.WIF = EncryptString(account.WIF, passwordHash, out string iv);
                        account.iv = iv;

                        // Decrypting to ensure there are no exceptions.
                        DecryptString(account.WIF, passwordHash, account.iv);
                    }
                    else
                    {
                        account.passwordProtected = false;
                    }

                    Accounts[i] = account;
                }

                SaveAccounts();
            }
            else if (!string.IsNullOrEmpty(wallets))
            {
                var bytes = Base16.Decode(wallets);
                Accounts = Serialization.Unserialize<Account[]>(bytes).ToList();
            }

            AccountsAreReadyToBeUsed = true;
        }

        public void SaveAccounts()
        {
            PlayerPrefs.SetInt(WalletVersionTag, 2);

            var bytes = Serialization.Serialize(Accounts.ToArray());
            PlayerPrefs.SetString(WalletTag, Base16.Encode(bytes));
            PlayerPrefs.Save();
        }

        private const string TokenInfoTag = "info.tokens";

        public string GetEthereumContract(string symbol)
        {
            string _return_value;

            switch (symbol.ToUpper())
            {
                case "SOUL":
                    switch (Settings.ethereumNetwork)
                    {
                        case EthereumNetwork.Main_Net:
                            _return_value = "79C75E2e8720B39e258F41c37cC4f309E0b0fF80";
                            break;

                        case EthereumNetwork.Ropsten:
                            _return_value = "19861B13425d8aCFB70eB91Ac50EC3cF721d0C8a";
                            break;

                        case EthereumNetwork.Local_Net:
                            _return_value = Settings.ethereumLocalnetSoulContract;
                            break;

                        default:
                            _return_value = "";
                            break;
                    }
                    break;

                case "KCAL":
                    switch (Settings.ethereumNetwork)
                    {
                        case EthereumNetwork.Main_Net:
                            _return_value = "14EB60F5f270B059B0c788De0Ddc51Da86f8a06d";
                            break;

                        case EthereumNetwork.Ropsten:
                            _return_value = "8218c82446bb74fB525fECC8844B03C34f987efe";
                            break;

                        case EthereumNetwork.Local_Net:
                            _return_value = Settings.ethereumLocalnetKcalContract;
                            break;

                        default:
                            _return_value = "";
                            break;
                    }
                    break;

                case "DAI":
                    switch (Settings.ethereumNetwork)
                    {
                        case EthereumNetwork.Main_Net:
                            _return_value = "";
                            break;

                        case EthereumNetwork.Ropsten:
                            _return_value = "";
                            break;

                        default:
                            _return_value = "";
                            break;
                    }
                    break;

                case "USDT":
                    switch (Settings.ethereumNetwork)
                    {
                        case EthereumNetwork.Main_Net:
                            _return_value = "";
                            break;

                        case EthereumNetwork.Ropsten:
                            _return_value = "";
                            break;

                        default:
                            _return_value = "";
                            break;
                    }
                    break;

                case "USDC":
                    switch (Settings.ethereumNetwork)
                    {
                        case EthereumNetwork.Main_Net:
                            _return_value = "";
                            break;

                        case EthereumNetwork.Ropsten:
                            _return_value = "";
                            break;

                        default:
                            _return_value = "";
                            break;
                    }
                    break;

                case "WBTC":
                    switch (Settings.ethereumNetwork)
                    {
                        case EthereumNetwork.Main_Net:
                            _return_value = "";
                            break;

                        case EthereumNetwork.Ropsten:
                            _return_value = "";
                            break;

                        default:
                            _return_value = "";
                            break;
                    }
                    break;

                case "MUU":
                    switch (Settings.ethereumNetwork)
                    {
                        case EthereumNetwork.Main_Net:
                            _return_value = "25836ce76065A3DfCeF069fD4964C240C4F2523F";
                            break;

                        case EthereumNetwork.Ropsten:
                            _return_value = "";
                            break;

                        case EthereumNetwork.Local_Net:
                            _return_value = "";
                            break;

                        default:
                            _return_value = "";
                            break;
                    }
                    break;

                case "DANK":
                    switch (Settings.ethereumNetwork)
                    {
                        case EthereumNetwork.Main_Net:
                            _return_value = "9ea1ae46c15a4164b74463bc26f8aa3b0eea2e6e";
                            break;

                        case EthereumNetwork.Ropsten:
                            _return_value = "";
                            break;

                        case EthereumNetwork.Local_Net:
                            _return_value = Settings.ethereumLocalnetDankContract;
                            break;

                        default:
                            _return_value = "";
                            break;
                    }
                    break;

                case "DYT":
                    switch (Settings.ethereumNetwork)
                    {
                        case EthereumNetwork.Main_Net:
                            _return_value = "740623d2c797b7D8D1EcB98e9b4Afcf99Ec31E14";
                            break;

                        case EthereumNetwork.Ropsten:
                            _return_value = "e7018AcAd667012D50eDB363eFfa4f2f56C6A0b0";
                            break;

                        case EthereumNetwork.Local_Net:
                            _return_value = Settings.ethereumLocalnetDytContract;
                            break;

                        default:
                            _return_value = "";
                            break;
                    }
                    break;

                default:
                    _return_value = "";
                    break;
            }

            Log.Write($"GetEthereumContract({symbol}): {_return_value}", Log.Level.Debug1);

            if (String.IsNullOrEmpty(_return_value))
                Log.WriteWarning($"Ethereum contract for {symbol} [{Settings.ethereumNetwork}] not found!");

            return _return_value;
        }

        private void PrepareTokens()
        {
            var extFlags = TokenFlags.Transferable.ToString() + "," + TokenFlags.Fungible.ToString() + "," + TokenFlags.Foreign.ToString() + "," + TokenFlags.Divisible.ToString();
            var pepFlags = TokenFlags.Transferable.ToString() + "," + TokenFlags.Fungible.ToString();
            var nftFlags = TokenFlags.Transferable.ToString();
            SupportedTokens = new List<Token>() {
                new Token() { symbol = "SOUL", apiSymbol = "phantasma", platform = DomainSettings.PlatformName, hash = "", decimals = 8, maxSupply = "0", name = "Phantasma Stake", flags = extFlags },
                new Token() { symbol = "KCAL", apiSymbol = "phantasma-energy", platform = DomainSettings.PlatformName, hash = "", decimals = 10, maxSupply = "0", name = "Phantasma Energy", flags = extFlags },
                new Token() { symbol = "NEO", apiSymbol = "neo", platform = DomainSettings.PlatformName, hash = "", decimals = 0, maxSupply = "100000000", name = "Neo", flags = extFlags },
                new Token() { symbol = "GAS", apiSymbol = "gas", platform = DomainSettings.PlatformName, hash = "", decimals = 8, maxSupply = "100000000", name = "GAS (Neo)", flags = extFlags },
                new Token() { symbol = "MKNI", apiSymbol = "", platform = DomainSettings.PlatformName, hash = Hash.FromString("MKNI").ToString(), decimals = 0, maxSupply = "1000000", name = "Mankini", flags = pepFlags },
                new Token() { symbol = "NACHO", apiSymbol = "", platform = DomainSettings.PlatformName, hash = Hash.FromString("NACHO").ToString(), decimals = 8, maxSupply = "1000000", name = "Nachos", flags = pepFlags },
                new Token() { symbol = "TTRS", apiSymbol = "", platform = DomainSettings.PlatformName, hash = Hash.FromString("TTRS").ToString(), decimals = 0, maxSupply = "0", name = "22series", flags = nftFlags },
                new Token() { symbol = "GHOST", apiSymbol = "", platform = DomainSettings.PlatformName, hash = Hash.FromString("GHOST").ToString(), decimals = 0, maxSupply = "0", name = "Ghost", flags = nftFlags },
                new Token() { symbol = "CROWN", apiSymbol = "", platform = DomainSettings.PlatformName, hash = Hash.FromString("CROWN").ToString(), decimals = 0, maxSupply = "0", name = "Crown", flags = nftFlags },
                new Token() { symbol = "GOATI", apiSymbol = "", platform = DomainSettings.PlatformName, hash = Hash.FromString("GOATI").ToString(), decimals = 3, maxSupply = "0", name = "GOATi", flags = pepFlags + "," + TokenFlags.Divisible.ToString() },
                new Token() { symbol = "DYT", apiSymbol = "dynamite", platform = DomainSettings.PlatformName, hash = Hash.FromString("DYT").ToString(), decimals = 18, maxSupply = "1500000", name = "DYT", flags = extFlags },
                new Token() { symbol = "ETH", apiSymbol = "ethereum", platform = DomainSettings.PlatformName, hash = "", decimals = 18, maxSupply = "0", name = "Ethereum", flags = extFlags },
                new Token() { symbol = "DAI", apiSymbol = "dai", platform = DomainSettings.PlatformName, hash = "", decimals = 18, maxSupply = "0", name = "Dai Stablecoin", flags = extFlags },
                new Token() { symbol = "USDT", apiSymbol = "tether", platform = DomainSettings.PlatformName, hash = "", decimals = 6, maxSupply = "0", name = "Tether USD", flags = extFlags },
                new Token() { symbol = "USDC", apiSymbol = "usd-coin", platform = DomainSettings.PlatformName, hash = "", decimals = 6, maxSupply = "0", name = "USD Coin", flags = extFlags },
                new Token() { symbol = "WBTC", apiSymbol = "wrapped-bitcoin", platform = DomainSettings.PlatformName, hash = "", decimals = 8, maxSupply = "0", name = "Wrapped BTC", flags = extFlags },
                new Token() { symbol = "MUU", apiSymbol = "", platform = DomainSettings.PlatformName, hash = Hash.FromString("MUU").ToString(), decimals = 18, maxSupply = "2000000000", name = "MUU", flags = extFlags },
                new Token() { symbol = "DANK", apiSymbol = "", platform = DomainSettings.PlatformName, hash = Hash.FromString("DANK").ToString(), decimals = 18, maxSupply = "50000000", name = "DANK", flags = extFlags },

                new Token() { symbol = "SOUL", apiSymbol = "phantasma", platform = "neo", hash = "ed07cffad18f1308db51920d99a2af60ac66a7b3", decimals = 8, maxSupply = "0", name = "Phantasma Stake", flags = extFlags },
                new Token() { symbol = "NEO", apiSymbol = "neo", platform = "neo", hash = "c56f33fc6ecfcd0c225c4ab356fee59390af8560be0e930faebe74a6daff7c9b", decimals = 0, maxSupply = "100000000", name = "Neo", flags = extFlags },
                new Token() { symbol = "GAS", apiSymbol = "gas", platform = "neo", hash = "602c79718b16e442de58778e148d0b1084e3b2dffd5de6b7b16cee7969282de7", decimals = 8, maxSupply = "100000000", name = "GAS (Neo)", flags = extFlags },
                new Token() { symbol = "SWTH", apiSymbol = "switcheo", platform = "neo", hash = "3e09e602eeeb401a2fec8e8ea137d59aae54a139", decimals = 8, maxSupply = "2160000000", name = "Switcheo", flags = extFlags },
                new Token() { symbol = "NEX", apiSymbol = "neon-exchange", platform = "neo", hash = "3a4acd3647086e7c44398aac0349802e6a171129", decimals = 8, maxSupply = "50000000", name = "Nex", flags = extFlags },
                new Token() { symbol = "TKY", apiSymbol = "thekey", platform = "neo", hash = "132947096727c84c7f9e076c90f08fec3bc17f18", decimals = 8, maxSupply = "1000000000", name = "The Key", flags = extFlags },
                new Token() { symbol = "CGAS", apiSymbol = "", platform = "neo", hash = "74f2dc36a68fdc4682034178eb2220729231db76", decimals = 8, maxSupply = "1000000000", name = "NEP5 GAS", flags = extFlags },
                new Token() { symbol = "MCT", apiSymbol = "master-contract-token", platform = "neo", hash = "a87cc2a513f5d8b4a42432343687c2127c60bc3f", decimals = 8, maxSupply = "1000000000", name = "Master Contract", flags = extFlags },
                new Token() { symbol = "DBC", apiSymbol = "deepbrain-chain", platform = "neo", hash = "b951ecbbc5fe37a9c280a76cb0ce0014827294cf", decimals = 8, maxSupply = "1000000000", name = "DeepBrain Coin", flags = extFlags },
                new Token() { symbol = "FTW", apiSymbol = "ftw", platform = "neo", hash = "11dbc2316f35ea031449387f615d9e4b0cbafe8b", decimals = 8, maxSupply = "1000000000", name = "For The Win", flags = extFlags },
                new Token() { symbol = "ACAT", apiSymbol = "alphacat", platform = "neo", hash = "7f86d61ff377f1b12e589a5907152b57e2ad9a7a", decimals = 8, maxSupply = "1000000000", name = "Alphacat", flags = extFlags },
                new Token() { symbol = "QLC", apiSymbol = "qlink", platform = "neo", hash = "0d821bd7b6d53f5c2b40e217c6defc8bbe896cf5", decimals = 8, maxSupply = "1000000000", name = "Qlink Token", flags = extFlags },
                new Token() { symbol = "TNC", apiSymbol = "trinity-network-credit", platform = "neo", hash = "08e8c4400f1af2c20c28e0018f29535eb85d15b6", decimals = 8, maxSupply = "1000000000", name = "Trinity Network Credit", flags = extFlags },
                new Token() { symbol = "PHX", apiSymbol = "red-pulse", platform = "neo", hash = "1578103c13e39df15d0d29826d957e85d770d8c9", decimals = 8, maxSupply = "1000000000", name = "Red Pulse Phoenix", flags = extFlags },
                new Token() { symbol = "LRN", apiSymbol = "loopring-neo", platform = "neo", hash = "06fa8be9b6609d963e8fc63977b9f8dc5f10895f", decimals = 8, maxSupply = "1000000000", name = "Loopring Neo Token", flags = extFlags },
                new Token() { symbol = "ASA", apiSymbol = "asura", platform = "neo", hash = "a58b56b30425d3d1f8902034996fcac4168ef71d", decimals = 8, maxSupply = "1000000000", name = "Asura World Coin", flags = extFlags },
                new Token() { symbol = "OBT", apiSymbol = "orbis-token", platform = "neo", hash = "0e86a40588f715fcaf7acd1812d50af478e6e917", decimals = 8, maxSupply = "1000000000", name = "Orbis", flags = extFlags },
                new Token() { symbol = "RHT", apiSymbol = "hashpuppy-token", platform = "neo", hash = "2328008e6f6c7bd157a342e789389eb034d9cbc4", decimals = 8, maxSupply = "1000000000", name = "HashPuppy Token", flags = extFlags },
                new Token() { symbol = "LX", apiSymbol = "lux", platform = "neo", hash = "bb3b54ab244b3658155f2db4429fc38ac4cef625", decimals = 8, maxSupply = "1000000000", name = "Moonlight Lux", flags = extFlags },
                new Token() { symbol = "BRDG", apiSymbol = "bridge-protocol", platform = "neo", hash = "78fd589f7894bf9642b4a573ec0e6957dfd84c48", decimals = 8, maxSupply = "1000000000", name = "Bridge Protocol", flags = extFlags },
                new Token() { symbol = "ALEPH", apiSymbol = "aleph", platform = "neo", hash = "2efdb22c152896964665d0a8214dc7bd59232162", decimals = 8, maxSupply = "0", name = "Aleph.im", flags = extFlags },
                new Token() { symbol = "TMN", apiSymbol = "translateme-network-token", platform = "neo", hash = "d613223fa138a1555ff711581982462acde209c5", decimals = 8, maxSupply = "0", name = "TranslateMe Network Token", flags = extFlags },
                new Token() { symbol = "FLM", apiSymbol = "flamingo-finance", platform = "neo", hash = "4d9eab13620fe3569ba3b0e56e2877739e4145e3", decimals = 8, maxSupply = "0", name = "Flamingo Finance", flags = extFlags },

                new Token() { symbol = "SOUL", apiSymbol = "phantasma", platform = "ethereum", hash = "", decimals = 8, maxSupply = "0", name = "Phantasma Stake", flags = extFlags },
                new Token() { symbol = "KCAL", apiSymbol = "phantasma-energy", platform = "ethereum", hash = "", decimals = 10, maxSupply = "0", name = "Phantasma Energy", flags = extFlags },
                new Token() { symbol = "ETH", apiSymbol = "ethereum", platform = "ethereum", hash = "", decimals = 18, maxSupply = "0", name = "Ethereum", flags = extFlags },
                new Token() { symbol = "DAI", apiSymbol = "dai", platform = "ethereum", hash = "", decimals = 18, maxSupply = "0", name = "Dai Stablecoin", flags = extFlags },
                new Token() { symbol = "USDT", apiSymbol = "tether", platform = "ethereum", hash = "", decimals = 6, maxSupply = "0", name = "Tether USD", flags = extFlags },
                new Token() { symbol = "USDC", apiSymbol = "usd-coin", platform = "ethereum", hash = "", decimals = 6, maxSupply = "0", name = "USD Coin", flags = extFlags },
                new Token() { symbol = "WBTC", apiSymbol = "wrapped-bitcoin", platform = "ethereum", hash = "", decimals = 8, maxSupply = "0", name = "Wrapped BTC", flags = extFlags },
                new Token() { symbol = "DYT", apiSymbol = "", platform = "ethereum", hash = "", decimals = 18, maxSupply = "0", name = "DYT", flags = extFlags },
                new Token() { symbol = "MUU", apiSymbol = "", platform = "ethereum", hash = "", decimals = 18, maxSupply = "0", name = "MUU", flags = extFlags },
                new Token() { symbol = "DANK", apiSymbol = "", platform = "ethereum", hash = "", decimals = 18, maxSupply = "0", name = "DANK", flags = extFlags }/*,
                new Token() { symbol = "MKNI", apiSymbol = "", platform = "ethereum", hash = "", decimals = 0, maxSupply = "1000000", name = "Mankini", flags = extFlags }*/
            };

            CurrentTokenCurrency = "";

            Log.Write($"{SupportedTokens.Count} tokens supported");
            Status = "ok";
        }

        public void RefreshTokenPrices()
        {
            bool needRefresh = false;

            if (CurrentTokenCurrency != Settings.currency)
            {
                needRefresh = true;
            }
            else
            {
                var diff = DateTime.UtcNow - _lastPriceUpdate;
                if (diff.TotalMinutes >= 5)
                {
                    needRefresh = true;
                }
            }


            if (needRefresh)
            {
                CurrentTokenCurrency = Settings.currency;
                _lastPriceUpdate = DateTime.UtcNow;

                var expectedFlag = TokenFlags.Foreign.ToString();
                var symbolList = SupportedTokens.Where(x => x.flags.Contains(expectedFlag));
                StartCoroutine(FetchTokenPrices(symbolList, CurrentTokenCurrency));
            }
        }

        public void UpdateAPIs()
        {
            Log.Write("reinit APIs => " + Settings.phantasmaRPCURL);
            phantasmaApi = new PhantasmaAPI(Settings.phantasmaRPCURL);
            ethereumApi = new EthereumAPI(Settings.ethereumRPCURL);
            neoApi = new NeoAPI(Settings.neoRPCURL, Settings.neoscanURL);

            // We should renew all interop addresses when switching between nets.
            // Otherwise we might send funds to wrong interop address.
            ClearInteropMap();
        }

        private void LoadNexus()
        {
            UpdateAPIs();

            PrepareTokens();

            /*var tokenList = PlayerPrefs.GetString(TokenInfoTag, "");

            if (!string.IsNullOrEmpty(tokenList))
            {
                var tokenBytes = Base16.Decode(tokenList);

                var tokens = Serialization.Unserialize<Token[]>(tokenBytes);

                return;
            }

            StartCoroutine(phantasmaApi.GetTokens((tokens) =>
            {
                PrepareTokens(tokens);
                var tokenBytes = Serialization.Serialize(tokens);
                PlayerPrefs.SetString(TokenInfoTag, Base16.Encode(tokenBytes));
                return;
            },
            (error, msg) =>
            {
                Status = "Failed to fetch token list...";
            }));*/
        }

        // Update is called once per frame
        void Update()
        {

        }

        public int GetTokenDecimals(string symbol, PlatformKind platform)
        {
            var token = SupportedTokens.Where(x => x.symbol == symbol && x.platform.ToUpper() == platform.ToString().ToUpper()).SingleOrDefault();
            if (token != null)
            {
                return token.decimals;
            }

            return -1;
        }

        public bool GetTokenBySymbol(string symbol, PlatformKind platform, out Token token)
        {
            token = SupportedTokens.Where(x => x.symbol == symbol && x.platform.ToUpper() == platform.ToString().ToUpper()).SingleOrDefault();
            if (token != null)
            {
                return true;
            }

            token = new Token();
            return false;
        }

        public decimal AmountFromString(string str, int decimals)
        {
            if (string.IsNullOrEmpty(str))
            {
                return 0;
            }

            var n = BigInteger.Parse(str);
            return UnitConversion.ToDecimal(n, decimals);
        }

        public void SignAndSendTransaction(string chain, byte[] script, byte[] payload, ProofOfWork PoW, IKeyPair customKeys, Action<Hash, string> callback, Func<byte[], byte[], byte[], byte[]> customSignFunction = null)
        {
            if (payload == null)
            {
                payload = System.Text.Encoding.UTF8.GetBytes(WalletIdentifier);
            }

            switch (CurrentPlatform)
            {
                case PlatformKind.Phantasma:
                    {
                        var keys = (customKeys != null) ? customKeys : PhantasmaKeys.FromWIF(CurrentWif);

                        StartCoroutine(phantasmaApi.SignAndSendTransactionWithPayload(keys, Settings.nexusName, script, chain, payload, PoW, (hashText) =>
                        {
                            var hash = Hash.Parse(hashText);
                            callback(hash, null);
                        }, (error, msg) =>
                        {
                            if(error == EPHANTASMA_SDK_ERROR_TYPE.WEB_REQUEST_ERROR)
                            {
                                ChangeFaultyRPCURL();
                            }
                            callback(Hash.Null, msg);
                        }, customSignFunction));
                        break;
                    }

                case PlatformKind.Neo:
                    {
                        try
                        {
                            var transfer = Serialization.Unserialize<TransferRequest>(script);

                            if (transfer.amount <=0)
                            {
                                callback(Hash.Null, $"invalid transfer amount: {transfer.amount}");
                            }
                            else
                            if (transfer.platform == CurrentPlatform)
                            {
                                switch (transfer.platform)
                                {
                                    case PlatformKind.Neo:
                                        {
                                            var keys = NeoKeys.FromWIF(transfer.key);

                                            StartCoroutine(neoApi.GetUnspent(keys.Address, (unspent) =>
                                            {
                                                Log.Write("Got unspents for " + keys.Address);

                                                if (transfer.symbol == "NEO" || transfer.symbol == "GAS")
                                                {
                                                    CoroutineUtils.StartThrowingCoroutine(this, neoApi.SendAsset((tx, error) =>
                                                    {
                                                        if (tx != null)
                                                        {
                                                            var hash = Hash.Parse(tx.Hash.ToString());
                                                            callback(hash, null);
                                                        }
                                                        else
                                                        {
                                                            callback(Hash.Null, error);
                                                        }
                                                    }, unspent, keys, transfer.destination, transfer.symbol, transfer.amount, transfer.interop, Settings.neoGasFee), ex => {
                                                        if (ex != null)
                                                        {
                                                            callback(Hash.Null, ex.ToString());
                                                        }
                                                    });
                                                }
                                                else
                                                {
                                                    Token token;

                                                    if (GetTokenBySymbol(transfer.symbol, PlatformKind.Neo, out token))
                                                    {
                                                        var amount = System.Numerics.BigInteger.Parse(UnitConversion.ToBigInteger(transfer.amount, token.decimals).ToString());

                                                        var nep5 = new NEP5(neoApi, token.hash);
                                                        CoroutineUtils.StartThrowingCoroutine(this, nep5.Transfer(unspent, keys, transfer.destination, amount, transfer.interop, Settings.neoGasFee,
                                                        (tx, error) =>
                                                        {
                                                            if (tx != null)
                                                            {
                                                                var hash = Hash.Parse(tx.Hash.ToString());
                                                                callback(hash, null);
                                                            }
                                                            else
                                                            {
                                                                callback(Hash.Null, error);
                                                            }
                                                        }), ex => {
                                                            if (ex != null)
                                                            {
                                                                callback(Hash.Null, ex.ToString());
                                                            }
                                                        });
                                                    }
                                                    else
                                                    {
                                                        callback(Hash.Null, "invalid token: "+transfer.symbol);
                                                    }
                                                }

                                            }));

                                            break;
                                        }
                                }
                                return;
                            }
                        }
                        catch (Exception e)
                        {
                            callback(Hash.Null, e.ToString());
                            return;
                        }

                        callback(Hash.Null, "something weird happened");
                        break;
                    }

                case PlatformKind.Ethereum:
                    {
                        try
                        {
                            var transfer = Serialization.Unserialize<TransferRequest>(script);

                            if (transfer.amount <= 0)
                            {
                                callback(Hash.Null, $"invalid transfer amount: {transfer.amount}");
                            }
                            else
                            if (transfer.platform == CurrentPlatform)
                            {
                                switch (transfer.platform)
                                {
                                    case PlatformKind.Ethereum:
                                        {
                                            var keys = EthereumKey.FromWIF(transfer.key);

                                            if (transfer.symbol == "ETH")
                                            {
                                                StartCoroutine(ethereumApi.GetNonce(keys.Address,
                                                (nonce) =>
                                                {
                                                    var hexTx = ethereumApi.SignTransaction(keys, nonce, transfer.destination,
                                                        UnitConversion.ToBigInteger(transfer.amount, 18), // Convert to WEI
                                                        Settings.ethereumGasPriceGwei * 1000000000, // Converting to WEI
                                                        Settings.ethereumTransferGasLimit);

                                                    StartCoroutine(ethereumApi.SendRawTransaction(hexTx, callback, (error, msg) =>
                                                    {
                                                        callback(Hash.Null, msg);
                                                    }));
                                                },
                                                (error, msg) =>
                                                {
                                                    callback(Hash.Null, msg);
                                                }));
                                            }
                                            else
                                            {
                                                if (GetTokenBySymbol(transfer.symbol, PlatformKind.Ethereum, out Token ethToken))
                                                {
                                                    StartCoroutine(ethereumApi.GetNonce(keys.Address,
                                                    (nonce) =>
                                                    {
                                                        var gasLimit = Settings.ethereumTokenTransferGasLimit;
                                                        if (SearchInteropMapForAddress(PlatformKind.Ethereum) == transfer.destination)
                                                        {
                                                            gasLimit = Settings.ethereumTokenTransferGasLimit;
                                                        }

                                                        var hexTx = ethereumApi.SignTokenTransaction(keys, nonce,
                                                            GetEthereumContract(ethToken.symbol),
                                                            transfer.destination,
                                                            UnitConversion.ToBigInteger(transfer.amount, ethToken.decimals),
                                                            Settings.ethereumGasPriceGwei * 1000000000, // Converting to WEI
                                                            gasLimit);

                                                        StartCoroutine(ethereumApi.SendRawTransaction(hexTx, callback, (error, msg) =>
                                                        {
                                                            callback(Hash.Null, msg);
                                                        }));
                                                    },
                                                    (error, msg) =>
                                                    {
                                                        callback(Hash.Null, msg);
                                                    }));
                                                }
                                                else
                                                {
                                                    callback(Hash.Null, $"Token {transfer.symbol} not supported");
                                                }
                                            }

                                            break;
                                        }
                                }
                                return;
                            }
                        }
                        catch (Exception e)
                        {
                            callback(Hash.Null, e.ToString());
                            return;
                        }

                        callback(Hash.Null, "something weird happened");
                        break;
                    }

                default:
                    {
                        callback(Hash.Null, "not implemented for " + CurrentPlatform);
                        break;
                    }
            }
        }

        public void InvokeScript(string chain, byte[] script, Action<byte[], string> callback)
        {
            var account = this.CurrentAccount;

            switch (CurrentPlatform)
            {
                case PlatformKind.Phantasma:
                    {
                        Log.Write("InvokeScript: " + System.Text.Encoding.UTF8.GetString(script), Log.Level.Debug1);
                        StartCoroutine(phantasmaApi.InvokeRawScript(chain, Base16.Encode(script), (x) =>
                        {
                            Log.Write("InvokeScript result: " + x.result, Log.Level.Debug1);
                            callback(Base16.Decode(x.result), null);
                        }, (error, log) =>
                        {
                            if (error == EPHANTASMA_SDK_ERROR_TYPE.WEB_REQUEST_ERROR)
                            {
                                ChangeFaultyRPCURL();
                            }
                            callback(null, log);
                        }));
                        break;
                    }
                default:
                    {
                        callback(null, "not implemented for " + CurrentPlatform);
                        break;
                    }
            }
        }

        public void GetArchive(Hash hash, Action<bool, Archive, string> callback)
        {
            var account = this.CurrentAccount;

            switch (CurrentPlatform)
            {
                case PlatformKind.Phantasma:
                    {
                        Log.Write("GetArchive: " + hash, Log.Level.Debug1);
                        StartCoroutine(phantasmaApi.GetArchive(hash.ToString(), (result) =>
                        {
                            Log.Write("GetArchive result: " + result, Log.Level.Debug1);
                            callback(true, result, null);
                        }, (error, log) =>
                        {
                            if (error == EPHANTASMA_SDK_ERROR_TYPE.WEB_REQUEST_ERROR)
                            {
                                ChangeFaultyRPCURL();
                            }
                            callback(false, new Archive(), log);
                        }));
                        break;
                    }
                default:
                    {
                        callback(false, new Archive(), "not implemented for " + CurrentPlatform);
                        break;
                    }
            }
        }

        public void ReadArchive(Hash hash, int blockIndex, Action<bool, byte[], string> callback)
        {
            var account = this.CurrentAccount;

            switch (CurrentPlatform)
            {
                case PlatformKind.Phantasma:
                    {
                        Log.Write("ReadArchive: " + hash, Log.Level.Debug1);
                        StartCoroutine(phantasmaApi.ReadArchive(hash.ToString(), blockIndex, (result) =>
                        {
                            Log.Write("ReadArchive result: " + result, Log.Level.Debug1);
                            callback(true, result, null);
                        }, (error, log) =>
                        {
                            if (error == EPHANTASMA_SDK_ERROR_TYPE.WEB_REQUEST_ERROR)
                            {
                                ChangeFaultyRPCURL();
                            }
                            callback(false, null, log);
                        }));
                        break;
                    }
                default:
                    {
                        callback(false, null, "not implemented for " + CurrentPlatform);
                        break;
                    }
            }
        }

        public void WriteArchive(Hash hash, int blockIndex, byte[] data, Action<bool, string> callback)
        {
            var account = this.CurrentAccount;

            switch (CurrentPlatform)
            {
                case PlatformKind.Phantasma:
                    {
                        Log.Write("WriteArchive: " + hash, Log.Level.Debug1);
                        StartCoroutine(phantasmaApi.WriteArchive(hash.ToString(), blockIndex, data, (result) =>
                        {
                            Log.Write("WriteArchive result: " + result, Log.Level.Debug1);
                            callback(result, null);
                        }, (error, log) =>
                        {
                            if (error == EPHANTASMA_SDK_ERROR_TYPE.WEB_REQUEST_ERROR)
                            {
                                ChangeFaultyRPCURL();
                            }
                            callback(false, log);
                        }));
                        break;
                    }
                default:
                    {
                        callback(false, "not implemented for " + CurrentPlatform);
                        break;
                    }
            }
        }

        private Action _refreshCallback;
        private DateTime _lastBalanceRefresh = DateTime.MinValue;
        private DateTime _lastNftRefresh = DateTime.MinValue;
        private string _lastNftRefreshSymbol = "";
        private DateTime _lastHistoryRefresh = DateTime.MinValue;

        public void SelectAccount(int index)
        {
            _lastBalanceRefresh = DateTime.MinValue;
            _lastNftRefresh = DateTime.MinValue;
            _lastNftRefreshSymbol = "";
            _lastHistoryRefresh = DateTime.MinValue;
            _selectedAccountIndex = index;
            CurrentPasswordHash = "";

            _accountInitialized = false;

            var platforms = CurrentAccount.platforms.Split();

            // We should add Ethereum platform to old accounts.
            if (!platforms.Contains(PlatformKind.Ethereum))
            {
                var account = Accounts[_selectedAccountIndex];
                account.platforms |= PlatformKind.Ethereum;
                Accounts[_selectedAccountIndex] = account;

                _states[PlatformKind.Ethereum] = new AccountState()
                {
                    platform = PlatformKind.Ethereum,
                    address = GetAddress(CurrentIndex, PlatformKind.Ethereum),
                    balances = new Balance[0],
                    flags = AccountFlags.None,
                    name = ValidationUtils.ANONYMOUS_NAME,
                };

                SaveAccounts();

                platforms.Add(PlatformKind.Ethereum);
            }

            CurrentPlatform = platforms.FirstOrDefault();
            _states.Clear();
        }

        public void UnselectAcount()
        {
            _selectedAccountIndex = -1;

            _accountInitialized = false;

            // revoke all dapps connected to this account via Phantasma Link
            if (_states.ContainsKey(PlatformKind.Phantasma))
            {
                var link = ConnectorManager.Instance.PhantasmaLink;

                var state = _states[PlatformKind.Phantasma];
                foreach (var entry in state.dappTokens)
                {
                    link.Revoke(entry.Key, entry.Value);
                }
            }

            _states.Clear();
            _nfts.Clear();
            TtrsStore.Clear();
            NftImages.Clear();
        }

        private void ReportWalletBalance(PlatformKind platform, AccountState state)
        {
            _pendingRequestCount--;

            if (state != null)
            {
                Log.Write("Received new state for " + platform);
                _states[platform] = state;

                if (!_accountInitialized && GetWorthOfPlatform(platform) > GetWorthOfPlatform(CurrentPlatform))
                {
                    CurrentPlatform = platform;
                }
            }

            if (_pendingRequestCount == 0)
            {
                _accountInitialized = true;
                InvokeRefreshCallback();
            }
        }

        private decimal GetWorthOfPlatform(PlatformKind platform)
        {
            if (!_states.ContainsKey(platform))
            {
                return 0;
            }

            decimal total = 0;
            var state = _states[platform];
            foreach (var balance in state.balances)
            {
                total += balance.Total;
            }
            return total;
        }

        private void ReportWalletNft(PlatformKind platform, string symbol)
        {
            _pendingRequestCount--;

            if (_nfts.ContainsKey(platform) && _nfts[platform] != null)
            {
                Log.Write($"Received {_nfts[platform].Count()} new {symbol} NFTs for {platform}");

                if (CurrentPlatform == PlatformKind.None)
                {
                    CurrentPlatform = platform;
                }
            }

            if (_pendingRequestCount == 0)
            {
                InvokeRefreshCallback();
            }
        }

        private void ReportWalletHistory(PlatformKind platform, List<HistoryEntry> history)
        {
            _pendingRequestCount--;

            if (history != null)
            {
                Log.Write("Received new history for " + platform);
                _history[platform] = history.ToArray();

                if (CurrentPlatform == PlatformKind.None)
                {
                    CurrentPlatform = platform;
                }
            }

            if (_pendingRequestCount == 0)
            {
                InvokeRefreshCallback();
            }
        }


        private const int maxChecks = 12; // Timeout after 36 seconds

        public void RequestConfirmation(string transactionHash, int checkCount, Action<string> callback)
        {
            switch (CurrentPlatform)
            {
                case PlatformKind.Phantasma:
                    StartCoroutine(phantasmaApi.GetTransaction(transactionHash, (tx) =>
                    {
                        callback(null);
                    }, (error, msg) =>
                    {
                        if (error == EPHANTASMA_SDK_ERROR_TYPE.WEB_REQUEST_ERROR)
                        {
                            ChangeFaultyRPCURL();
                        }

                        if (checkCount <= maxChecks)
                        {
                            callback(msg);
                        }
                        else
                        {
                            callback("timeout");
                        }
                    }));
                    break;

                case PlatformKind.Neo:
                    var url = GetNeoscanAPIUrl($"get_transaction/{transactionHash}");

                    StartCoroutine(WebClient.RESTRequest(url, WebClient.NoTimeout, (error, msg) =>
                    {
                        if (checkCount <= maxChecks)
                        {
                            callback("pending");
                        }
                        else
                        {
                            callback("timeout");
                        }
                    },
                    (response) =>
                    {
                        if (response.HasNode("vouts"))
                        {
                            callback(null);
                        }
                        else
                        {
                            if (checkCount <= maxChecks)
                            {
                                callback("pending");
                            }
                            else
                            {
                                callback("timeout");
                            }
                        }
                    }));
                    break;

                case PlatformKind.Ethereum:
                    // For Ethereum we should return immediately
                    // since it's unpredictable if we would be able to find tx in mempool
                    // or it will appear there after several minutes.
                    // And we are not waiting for a confirmation anyway.
                    callback(null);
                    break;

                default:
                    callback("not implemented: " + CurrentPlatform);
                    break;
            }

        }

        private void InvokeRefreshCallback()
        {
            var temp = _refreshCallback;
            _refreshCallback = null;
            temp?.Invoke();
        }

        public void RefreshBalances(bool force, Action callback = null)
        {
            var now = DateTime.UtcNow;
            var diff = now - _lastBalanceRefresh;

            if (!force && diff.TotalSeconds < 30)
            {
                InvokeRefreshCallback();
                return;
            }

            _lastBalanceRefresh = now;
            _refreshCallback = callback;

            var platforms = CurrentAccount.platforms.Split();
            _pendingRequestCount = platforms.Count;

            var wif = CurrentWif;

            foreach (var platform in platforms)
            {
                switch (platform)
                {
                    case PlatformKind.Phantasma:
                        {
                            var keys = PhantasmaKeys.FromWIF(wif);
                            var ethKeys = EthereumKey.FromWIF(wif);
                            StartCoroutine(phantasmaApi.GetAccount(keys.Address.Text, (acc) =>
                            {
                                var balanceMap = new Dictionary<string, Balance>();

                                foreach (var entry in acc.balances)
                                {
                                    balanceMap[entry.symbol] = new Balance()
                                    {
                                        Symbol = entry.symbol,
                                        Available = AmountFromString(entry.amount, GetTokenDecimals(entry.symbol, PlatformKind.Phantasma)),
                                        Pending = 0,
                                        Staked = 0,
                                        Claimable = 0,
                                        Chain = entry.chain,
                                        Decimals = GetTokenDecimals(entry.symbol, PlatformKind.Phantasma),
                                        Ids = entry.ids
                                    };
                                }

                                var stakedAmount = AmountFromString(acc.stake.amount, GetTokenDecimals("SOUL", PlatformKind.Phantasma));
                                var claimableAmount = AmountFromString(acc.stake.unclaimed, GetTokenDecimals("KCAL", PlatformKind.Phantasma));

                                var stakeTimestamp = new Timestamp(acc.stake.time);

                                if (stakedAmount > 0)
                                {
                                    var symbol = "SOUL";
                                    if (balanceMap.ContainsKey(symbol))
                                    {
                                        var entry = balanceMap[symbol];
                                        entry.Staked = stakedAmount;
                                    }
                                    else
                                    {
                                        var entry = new Balance()
                                        {
                                            Symbol = symbol,
                                            Chain = "main",
                                            Available = 0,
                                            Staked = stakedAmount,
                                            Claimable = 0,
                                            Pending = 0,
                                            Decimals = GetTokenDecimals(symbol, PlatformKind.Phantasma)
                                        };
                                        balanceMap[symbol] = entry;
                                    }
                                }

                                if (claimableAmount > 0)
                                {
                                    var symbol = "KCAL";
                                    if (balanceMap.ContainsKey(symbol))
                                    {
                                        var entry = balanceMap[symbol];
                                        entry.Claimable = claimableAmount;
                                    }
                                    else
                                    {
                                        var entry = new Balance()
                                        {
                                            Symbol = symbol,
                                            Chain = "main",
                                            Available = 0,
                                            Staked = 0,
                                            Claimable = claimableAmount,
                                            Pending = 0,
                                            Decimals = GetTokenDecimals(symbol, PlatformKind.Phantasma)
                                        };
                                        balanceMap[symbol] = entry;
                                    }
                                }

                                RequestPendings(keys.Address.Text, (phaSwaps, phaError) =>
                                {
                                    if (phaSwaps != null)
                                    {
                                        MergeSwaps(PlatformKind.Phantasma, balanceMap, phaSwaps);
                                    }
                                    else
                                    {
                                        Log.WriteWarning(phaError);
                                    }

                                    RequestPendings(ethKeys.Address, (swaps, error) =>
                                    {
                                        if (swaps != null)
                                        {
                                            MergeSwaps(PlatformKind.Phantasma, balanceMap, swaps);
                                        }
                                        else
                                        {
                                            Log.WriteWarning(error);
                                        }


                                        var state = new AccountState()
                                        {
                                            platform = platform,
                                            address = acc.address,
                                            name = acc.name,
                                            balances = balanceMap.Values.ToArray(),
                                            flags = AccountFlags.None
                                        };

                                        if (stakedAmount >= SoulMasterStakeAmount)
                                        {
                                            state.flags |= AccountFlags.Master;
                                        }

                                        if (acc.validator.Equals("Primary") || acc.validator.Equals("Secondary"))
                                        {
                                            state.flags |= AccountFlags.Validator;
                                        }

                                        state.stakeTime = stakeTimestamp;

                                        state.usedStorage = acc.storage.used;
                                        state.availableStorage = acc.storage.available;
                                        state.archives = acc.storage.archives;
                                        state.avatarData = acc.storage.avatar;

                                        ReportWalletBalance(platform, state);
                                    });
                                });
                            },
                            (error, msg) =>
                            {
                                if (error == EPHANTASMA_SDK_ERROR_TYPE.WEB_REQUEST_ERROR)
                                {
                                    ChangeFaultyRPCURL();
                                }
                                ReportWalletBalance(platform, null);
                            }));
                        }
                        break;

                    case PlatformKind.Neo:
                        {
                            var keys = NeoKeys.FromWIF(wif);

                            var url = GetNeoscanAPIUrl($"get_balance/{keys.Address}");

                            StartCoroutine(WebClient.RESTRequest(url, WebClient.DefaultTimeout, (error, msg) =>
                            {
                                ReportWalletBalance(platform, null);
                            },
                            (response) =>
                            {
                                var neoTokens = SupportedTokens.Where(x => x.platform.ToUpper() == PlatformKind.Neo.ToString().ToUpper());

                                var balances = new List<Balance>();

                                var balance = response.GetNode("balance");
                                foreach (var entry in balance.Children)
                                {
                                    var hash = entry.GetString("asset_hash");
                                    var symbol = entry.GetString("asset_symbol");
                                    var amount = entry.GetDecimal("amount");

                                    Token token;

                                    if (GetTokenBySymbol(symbol, PlatformKind.Neo, out token))
                                    {
                                        if (hash == token.hash)
                                        {
                                            balances.Add(new Balance()
                                            {
                                                Symbol = symbol,
                                                Available = amount,
                                                Pending = 0,
                                                Claimable = 0, // TODO support claimable GAS
                                                Staked = 0,
                                                Chain = "main",
                                                Decimals = token.decimals
                                            });
                                        }
                                    }
                                }

                                CoroutineUtils.StartThrowingCoroutine(this, neoApi.GetUnclaimed(keys.Address, (amount) =>
                                {
                                    RequestPendings(keys.Address, (swaps, error) =>
                                    {
                                        var balanceMap = new Dictionary<string, Balance>();

                                        foreach (var neoToken in neoTokens)
                                        {
                                            var tokenBalance = balances.Where(x => x.Symbol.ToUpper() == neoToken.symbol.ToUpper()).SingleOrDefault();

                                            if (tokenBalance != null)
                                            {
                                                balanceMap[tokenBalance.Symbol] = tokenBalance;

                                                if (tokenBalance.Symbol.ToUpper() == "GAS")
                                                {
                                                    tokenBalance.Claimable += amount;
                                                }
                                            }
                                            else
                                            {
                                                if (neoToken.symbol.ToUpper() == "GAS" && amount > 0)
                                                {
                                                    // We should show GAS even if its balance is 0
                                                    // if there's some GAS to be claimed.
                                                    balanceMap[neoToken.symbol] = new Balance()
                                                    {
                                                        Symbol = neoToken.symbol,
                                                        Available = 0,
                                                        Pending = 0,
                                                        Claimable = amount,
                                                        Staked = 0,
                                                        Chain = "main",
                                                        Decimals = neoToken.decimals
                                                    };
                                                }
                                            }
                                        }

                                        if (swaps != null)
                                        {
                                            MergeSwaps(PlatformKind.Neo, balanceMap, swaps);
                                        }
                                        else
                                        {
                                            Log.WriteWarning(error);
                                        }

                                        var state = new AccountState()
                                        {
                                            platform = platform,
                                            address = keys.Address,
                                            name = ValidationUtils.ANONYMOUS_NAME, // TODO support NNS
                                            balances = balanceMap.Values.ToArray(),
                                            flags = AccountFlags.None
                                        };
                                        ReportWalletBalance(platform, state);
                                    });
                                }), ex =>
                                {
                                    if (ex != null)
                                    {
                                        ReportWalletBalance(platform, null);
                                    }
                                });

                            }));
                        }
                        break;

                    case PlatformKind.Ethereum:
                        {
                            var keys = EthereumKey.FromWIF(wif);

                            var ethTokens = SupportedTokens.Where(x => x.platform.ToUpper() == PlatformKind.Ethereum.ToString().ToUpper());
                            var balances = new List<Balance>();

                            Action onLoadFinish = new Action(() =>
                            {
                                RequestPendings(keys.Address, (swaps, error) =>
                                {
                                    var balanceMap = new Dictionary<string, Balance>();
                                    foreach (var ethToken in ethTokens)
                                    {
                                        var tokenBalance = balances.Where(x => x.Symbol.ToUpper() == ethToken.symbol.ToUpper()).SingleOrDefault();
                                        if (tokenBalance != null)
                                            balanceMap[tokenBalance.Symbol] = tokenBalance;
                                    }

                                    if (swaps != null)
                                    {
                                        MergeSwaps(PlatformKind.Ethereum, balanceMap, swaps);
                                    }
                                    else
                                    {
                                        Log.WriteWarning(error);
                                    }

                                    var ethereumAddressUtil = new Phantasma.Ethereum.Util.AddressUtil();

                                    var state = new AccountState()
                                    {
                                        platform = platform,
                                        address = ethereumAddressUtil.ConvertToChecksumAddress(keys.Address),
                                        name = ValidationUtils.ANONYMOUS_NAME, // TODO support NNS
                                        balances = balanceMap.Values.ToArray(),
                                        flags = AccountFlags.None
                                    };
                                    ReportWalletBalance(platform, state);
                                });
                            });

                            foreach (var ethToken in ethTokens)
                            {
                                if (ethToken.symbol == "ETH")
                                {
                                    StartCoroutine(ethereumApi.GetBalance(keys.Address, ethToken.symbol, ethToken.decimals, (balance) =>
                                    {
                                        balances.Add(balance);

                                        if (balances.Count() == ethTokens.Count())
                                        {
                                            onLoadFinish();
                                        }
                                    },
                                    (error, msg) =>
                                    {
                                        ReportWalletBalance(platform, null);
                                    }));
                                }
                                else
                                {
                                    StartCoroutine(ethereumApi.GetTokenBalance(keys.Address,
                                        GetEthereumContract(ethToken.symbol),
                                        ethToken.symbol, ethToken.decimals, (balanceSoul) =>
                                    {
                                        balances.Add(balanceSoul);

                                        if (balances.Count() == ethTokens.Count())
                                        {
                                            onLoadFinish();
                                        }
                                    },
                                    (error, msg) =>
                                    {
                                        ReportWalletBalance(platform, null);
                                    }));
                                }
                            }
                        }
                        break;

                    default:
                        ReportWalletBalance(platform, null);
                        break;
                }
            }
        }

        public void BlankState()
        {
            var platforms = CurrentAccount.platforms.Split();

            _states.Clear();
            foreach (var platform in platforms)
            {
                _states[platform] = new AccountState()
                {
                    platform = platform,
                    address = GetAddress(CurrentIndex, platform),
                    balances = new Balance[0],
                    flags = AccountFlags.None,
                    name = ValidationUtils.ANONYMOUS_NAME,
                };
            }
        }

        private void MergeSwaps(PlatformKind platform, Dictionary<string, Balance> balanceMap, Swap[] swaps)
        {
            var platformName = platform.ToString().ToLower();

            foreach (var swap in swaps)
            {
                if (swap.destinationPlatform != platformName)
                {
                    continue;
                }

                if (swap.destinationHash != "pending")
                {
                    continue;
                }

                var decimals = GetTokenDecimals(swap.symbol, platform);
                var amount = AmountFromString(swap.value, decimals);

                Log.Write($"Found pending {platformName} swap: {amount} {swap.symbol}");

                if (balanceMap.ContainsKey(swap.symbol))
                {
                    var entry = balanceMap[swap.symbol];
                    entry.Pending += amount;
                    entry.PendingHash = swap.sourceHash;
                    entry.PendingPlatform = swap.sourcePlatform;
                }
                else
                {
                    var entry = new Balance()
                    {
                        Symbol = swap.symbol,
                        Chain = "main",
                        Available = 0,
                        Staked = 0,
                        Claimable = 0,
                        Pending = amount,
                        Decimals = decimals,
                        PendingHash = swap.sourceHash,
                        PendingPlatform = swap.sourcePlatform,
                    };
                    balanceMap[swap.symbol] = entry;
                }
            }
        }

        internal void InitDemoAccounts(NexusKind nexusKind)
        {
            var accounts = new List<Account>();

            /*
            if (nexusKind != NexusKind.Main_Net)
            {
                accounts.Add(new Account() { name = "genesis", platforms = PlatformKind.Phantasma | PlatformKind.Neo, WIF = "L2LGgkZAdupN2ee8Rs6hpkc65zaGcLbxhbSDGq8oh6umUxxzeW25", password = "lol", misc = "" });
                accounts.Add(new Account() { name = "bill", platforms = PlatformKind.Neo, WIF = "KxDgvEKzgSBPPfuVfw67oPQBSjidEiqTHURKSDL1R7yGaGYAeYnr", password = "mankini", misc = "" });
            }
            //new Account() { name = "zion", platforms = PlatformKind.Neo, key = "KwVG94yjfVg1YKFyRxAGtug93wdRbmLnqqrFV6Yd2CiA9KZDAp4H", password = "", misc = "" },

            if (nexusKind == NexusKind.Local_Net)
            {
                accounts.Add(new Account() { name = "other", platforms = PlatformKind.Phantasma | PlatformKind.Neo, WIF = "Kweyrx8ypkoPfzMsxV4NtgH8vXCWC1s1Dn3c2KJ4WAzC5nkyNt3e", password = "", misc = "" });
                accounts.Add(new Account() { name = "monk", platforms = PlatformKind.Phantasma | PlatformKind.Neo, WIF = "Kx4GzZxzGZsQNt8URu36SnvR5KGSzg8s8ZxH8cunzZGh2JLmxHsW", password = "", misc = "" });
            }
            */

            this.Accounts = accounts;
            SaveAccounts();
        }

        internal void DeleteAll()
        {
            this.Accounts = new List<Account>();
        }

        public void RefreshNft(bool force, string symbol, Action callback = null)
        {
            var now = DateTime.UtcNow;
            var diff = now - _lastNftRefresh;

            if (!force && diff.TotalSeconds < 30 && _lastNftRefreshSymbol == symbol)
            {
                InvokeRefreshCallback();
                return;
            }

            if (force)
            {
                // On force refresh we clear NFT symbol's cache.
                if (symbol.ToUpper() == "TTRS")
                    TtrsStore.Clear();
                else
                    Cache.ClearDataNode("tokens-" + symbol.ToLower(), Cache.FileType.JSON, CurrentState.address);

                NftImages.Clear(symbol);
            }

            _lastNftRefresh = now;
            _lastNftRefreshSymbol = symbol;
            _refreshCallback = callback;

            var platforms = CurrentAccount.platforms.Split();
            _pendingRequestCount = platforms.Count;

            var wif = this.CurrentWif;

            foreach (var platform in platforms)
            {
                // Reinitializing NFT dictionary if needed.
                if (_nfts.ContainsKey(platform))
                    _nfts[platform].Clear();

                if (GetTokenBySymbol(symbol, platform, out var tokenInfo))
                {
                    switch (platform)
                    {
                        case PlatformKind.Phantasma:
                            {
                                var keys = PhantasmaKeys.FromWIF(wif);

                                Log.Write("Getting NFTs...");
                                foreach (var balanceEntry in CurrentState.balances)
                                {
                                    if (balanceEntry.Symbol == symbol && !tokenInfo.flags.Contains(TokenFlags.Fungible.ToString()))
                                    {
                                        nftDescriptionsAreFullyLoaded = false;

                                        // Initializing NFT dictionary if needed.
                                        if (!_nfts.ContainsKey(platform))
                                            _nfts.Add(platform, new List<TokenData>());

                                        var cache = Cache.GetDataNode("tokens-" + symbol.ToLower(), Cache.FileType.JSON, 0, CurrentState.address);

                                        if (cache == null)
                                        {
                                            cache = DataNode.CreateObject();
                                        }
                                        DataNode cachedTokens;
                                        if (cache.HasNode("tokens-" + symbol.ToLower()))
                                            cachedTokens = cache.GetNode("tokens-" + symbol.ToLower());
                                        else
                                            cachedTokens = cache.AddNode(DataNode.CreateObject("tokens-" + symbol.ToLower()));

                                        int loadedTokenCounter = 0;
                                        foreach (var id in balanceEntry.Ids)
                                        {
                                            // Checking if token is cached.
                                            DataNode token = null;
                                            foreach (var cachedToken in cachedTokens.Children)
                                            {
                                                if (cachedToken.GetString("id") == id)
                                                {
                                                    token = cachedToken;
                                                    break;
                                                }
                                            }

                                            if (token != null)
                                            {
                                                // Loading token from cache.
                                                var tokenId = token.GetString("id");

                                                loadedTokenCounter++;

                                                // Checking if token already loaded to dictionary.
                                                if (!_nfts[platform].Exists(x => x.ID == tokenId))
                                                {
                                                    var tokenData = TokenData.FromNode(token, symbol);
                                                    _nfts[platform].Add(tokenData);

                                                    // Downloading NFT images.
                                                    StartCoroutine(NftImages.DownloadImage(symbol, tokenData.GetPropertyValue("ImageURL"), id));
                                                }

                                                if (loadedTokenCounter == balanceEntry.Ids.Length)
                                                {
                                                    // We finished loading tokens.
                                                    // Saving them in cache.
                                                    Cache.AddDataNode("tokens-" + symbol.ToLower(), Cache.FileType.JSON, cache, CurrentState.address);
                                                    
                                                    if (symbol != "TTRS")
                                                    {
                                                        // For all NFTs except TTRS all needed information
                                                        // is loaded by this moment.
                                                        nftDescriptionsAreFullyLoaded = true;
                                                    }

                                                    ReportWalletNft(platform, symbol);
                                                }
                                            }
                                            else
                                            {
                                                if (symbol == "TTRS")
                                                {
                                                    // TODO: Load TokenData for TTRS too (add batch load method for TokenDatas).
                                                    // For now we skip TokenData loading to speed up TTRS NFTs loading,
                                                    // since it's not used for TTRS anyway.
                                                    var tokenData = new TokenData();
                                                    tokenData.ID = id;
                                                    _nfts[platform].Add(tokenData);

                                                    loadedTokenCounter++;
                                                }
                                                else
                                                {
                                                    StartCoroutine(phantasmaApi.GetNFT(symbol, id, (result) =>
                                                    {
                                                        var tokenData = TokenData.FromNode(result, symbol);
                                                        
                                                        // Downloading NFT images.
                                                        StartCoroutine(NftImages.DownloadImage(symbol, tokenData.GetPropertyValue("ImageURL"), id));

                                                        loadedTokenCounter++;

                                                        token = cachedTokens.AddNode(result);

                                                        _nfts[platform].Add(tokenData);

                                                        if (loadedTokenCounter == balanceEntry.Ids.Length)
                                                        {
                                                            // We finished loading tokens.
                                                            // Saving them in cache.
                                                            Cache.AddDataNode("tokens-" + symbol.ToLower(), Cache.FileType.JSON, cache, CurrentState.address);

                                                            ReportWalletNft(platform, symbol);
                                                        }
                                                    }, (error, msg) =>
                                                    {
                                                        Log.Write(msg);
                                                    }));
                                                }
                                            }
                                        }

                                        ReportWalletNft(platform, symbol);

                                        if (balanceEntry.Ids.Length > 0)
                                        {
                                            // Getting NFT descriptions.
                                            if (symbol == "TTRS")
                                            {
                                                StartCoroutine(TtrsStore.LoadStoreNft(balanceEntry.Ids, (item) =>
                                                {
                                                    // Downloading NFT images.
                                                    StartCoroutine(NftImages.DownloadImage(symbol, item.ImageUrl, item.Id));
                                                }, () =>
                                                {
                                                    nftDescriptionsAreFullyLoaded = true;
                                                }));
                                            }
                                        }
                                    }
                                }
                            }
                            break;

                        default:
                            ReportWalletNft(platform, symbol);
                            break;
                    }
                }
                else
                {
                    ReportWalletNft(platform, symbol);
                }
            }
        }

        public void RefreshHistory(bool force, Action callback = null)
        {
            var now = DateTime.UtcNow;
            var diff = now - _lastHistoryRefresh;

            if (!force && diff.TotalSeconds < 30)
            {
                InvokeRefreshCallback();
                return;
            }

            _lastBalanceRefresh = now;
            _refreshCallback = callback;

            var platforms = CurrentAccount.platforms.Split();
            _pendingRequestCount = platforms.Count;

            var wif = this.CurrentWif;

            foreach (var platform in platforms)
            {
                switch (platform)
                {
                    case PlatformKind.Phantasma:
                        {
                            var keys = PhantasmaKeys.FromWIF(wif);
                            StartCoroutine(phantasmaApi.GetAddressTransactions(keys.Address.Text, 1, 20, (x, page, max) =>
                            {
                                var history = new List<HistoryEntry>();

                                foreach (var tx in x.txs)
                                {
                                    history.Add(new HistoryEntry()
                                    {
                                        hash = tx.hash,
                                        date = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc).AddSeconds(tx.timestamp).ToLocalTime(),
                                        url = GetPhantasmaTransactionURL(tx.hash)
                                    });
                                }

                                ReportWalletHistory(platform, history);
                            },
                            (error, msg) =>
                            {
                                if (error == EPHANTASMA_SDK_ERROR_TYPE.WEB_REQUEST_ERROR)
                                {
                                    ChangeFaultyRPCURL();
                                }
                                ReportWalletHistory(platform, null);
                            }));
                        }
                        break;

                    case PlatformKind.Neo:
                        {
                            var keys = NeoKeys.FromWIF(wif);
                            var url = GetNeoscanAPIUrl($"get_address_abstracts/{keys.Address}/1");

                            StartCoroutine(WebClient.RESTRequest(url, WebClient.DefaultTimeout, (error, msg) =>
                            {
                                ReportWalletHistory(platform, null);
                            },
                            (response) =>
                            {
                                var alreadyAddedHashes = new List<string>(); // Neoscan sends some transactions twice, should filter them.

                                var history = new List<HistoryEntry>();

                                var entries = response.GetNode("entries");
                                foreach (var entry in entries.Children)
                                {
                                    var hash = entry.GetString("txid");
                                    if (alreadyAddedHashes.Contains(hash) == false)
                                    {
                                        var time = entry.GetUInt32("time");

                                        history.Add(new HistoryEntry()
                                        {
                                            hash = hash,
                                            date = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc).AddSeconds(time).ToLocalTime(),
                                            url = GetNeoscanTransactionURL(hash),
                                        });

                                        alreadyAddedHashes.Add(hash);
                                    }
                                }

                                ReportWalletHistory(platform, history);
                            }));
                        }
                        break;

                    case PlatformKind.Ethereum:
                        {
                            var keys = EthereumKey.FromWIF(wif);
                            var urlEth = GetEtherscanAPIUrl($"module=account&action=txlist&address={keys.Address}&sort=desc");

                            StartCoroutine(WebClient.RESTRequest(urlEth, WebClient.DefaultTimeout, (error, msg) =>
                            {
                                ReportWalletHistory(platform, null);
                            },
                            (responseEth) =>
                            {
                                var urlErc20 = GetEtherscanAPIUrl($"module=account&action=tokentx&address={keys.Address}&sort=desc");
                                StartCoroutine(WebClient.RESTRequest(urlErc20, WebClient.DefaultTimeout, (error, msg) =>
                                {
                                    ReportWalletHistory(platform, null);
                                },
                                (responseErc20) =>
                                {
                                    var ethHistory = new Dictionary<string, DateTime>();

                                    // Adding ETH transactions to the dict.
                                    if (responseEth != null)
                                    {
                                        var entries = responseEth.GetNode("result");
                                        foreach (var entry in entries.Children)
                                        {
                                            var hash = entry.GetString("hash");
                                            if (!ethHistory.Any(x => x.Key == hash))
                                            {
                                                ethHistory.Add(hash, new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc).AddSeconds(entry.GetUInt32("timeStamp")).ToLocalTime());
                                            }
                                        }
                                    }

                                    // Adding ERC20 transactions to the dict.
                                    if (responseErc20 != null)
                                    {
                                        var entries = responseErc20.GetNode("result");
                                        foreach (var entry in entries.Children)
                                        {
                                            var hash = entry.GetString("hash");
                                            if (!ethHistory.Any(x => x.Key == hash))
                                            {
                                                ethHistory.Add(hash, new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc).AddSeconds(entry.GetUInt32("timeStamp")).ToLocalTime());
                                            }
                                        }
                                    }

                                    // Sorting tx-es by date.
                                    var orderedEthHistory = ethHistory.OrderByDescending(x => x.Value);

                                    var history = new List<HistoryEntry>();

                                    foreach (var entry in orderedEthHistory)
                                    {
                                        history.Add(new HistoryEntry()
                                        {
                                            hash = entry.Key,
                                            date = entry.Value,
                                            url = GetEtherscanTransactionURL(entry.Key),
                                        });
                                    }

                                    ReportWalletHistory(platform, history);
                                }));
                            }));
                        }
                        break;

                    default:
                        ReportWalletHistory(platform, null);
                        break;
                }
            }
        }

        public string GetPhantasmaTransactionURL(string hash)
        {
            var url = Settings.phantasmaExplorer;
            if (!url.EndsWith("/"))
            {
                url += "/";
            }

            return $"{url}tx/{hash}";
        }

        public string GetPhantasmaAddressURL(string address)
        {
            var url = Settings.phantasmaExplorer;
            if (!url.EndsWith("/"))
            {
                url += "/";
            }

            return $"{url}address/{address}";
        }

        public string GetPhantasmaContractURL(string symbol)
        {
            var url = Settings.phantasmaExplorer;
            if (!url.EndsWith("/"))
            {
                url += "/";
            }

            return $"{url}contract/{symbol}";
        }

        public string GetPhantasmaNftURL(string symbol, string tokenId)
        {
            var url = Settings.phantasmaNftExplorer;
            if (!url.EndsWith("/"))
            {
                url += "/";
            }

            return $"{url}{symbol.ToLower()}/{tokenId}";
        }

        private void RequestPendings(string address, Action<Swap[], string> callback)
        {
            StartCoroutine(phantasmaApi.GetSwapsForAddress(address, (swaps) =>
            {
                callback(swaps, null);
            }, (error, msg) =>
            {
                if (error == EPHANTASMA_SDK_ERROR_TYPE.WEB_REQUEST_ERROR)
                {
                    ChangeFaultyRPCURL();
                }
                callback(null, msg);
            }));
        }

        public string GetEtherscanTransactionURL(string hash)
        {
            if (!hash.StartsWith("0x"))
                hash = "0x" + hash;

            switch (Settings.ethereumNetwork)
            {
                case EthereumNetwork.Main_Net:
                    return $"https://etherscan.io/tx/{hash}";

                case EthereumNetwork.Ropsten:
                    return $"https://ropsten.etherscan.io/tx/{hash}";

                default:
                    return null;
            }
        }

        public string GetEtherscanAddressURL(string address)
        {
            if (!address.StartsWith("0x"))
                address = "0x" + address;

            switch (Settings.ethereumNetwork)
            {
                case EthereumNetwork.Main_Net:
                    return $"https://etherscan.io/address/{address}";

                case EthereumNetwork.Ropsten:
                    return $"https://ropsten.etherscan.io/address/{address}";

                default:
                    return null;
            }
        }

        private string GetEtherscanAPIUrl(string request)
        {
            if (string.IsNullOrEmpty(etherscanAPIToken))
            {
                return null;
            }

            switch (Settings.ethereumNetwork)
            {
                case EthereumNetwork.Main_Net:
                    return $"https://api.etherscan.io/api?apikey={etherscanAPIToken}&{request}";

                case EthereumNetwork.Ropsten:
                    return $"https://api-ropsten.etherscan.io/api?apikey={etherscanAPIToken}&{request}";

                default:
                    return null;
            }
        }

        public string GetNeoscanTransactionURL(string hash)
        {
            var url = Settings.neoscanURL;
            if (!url.EndsWith("/"))
            {
                url += "/";
            }

            return $"{url}transaction/{hash}";
        }
        public string GetNeoscanAddressURL(string address)
        {
            var url = Settings.neoscanURL;
            if (!url.EndsWith("/"))
            {
                url += "/";
            }

            return $"{url}address/{address}";
        }

        private string GetNeoscanAPIUrl(string request)
        {
            var url = Settings.neoscanURL;

            if (!url.EndsWith("/"))
            {
                url += "/";
            }

            return $"{url}api/main_net/v1/{request}";
        }

        internal bool SwapSupported(string symbol)
        {
            return symbol == "SOUL" || symbol == "NEO" || symbol == "GAS";
        }

        public int AddWallet(string name, PlatformKind platforms, string wif, string password)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 3)
            {
                throw new Exception("Name is too short.");
            }

            if (name.Length > 16)
            {
                throw new Exception("Name is too long.");
            }

            for (int i = 0; i < Accounts.Count(); i++)
            {
                if (Accounts[i].name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception("An account with this name already exists.");
                }
            }

            var account = new Account() { name = name, platforms = platforms, misc = "" };

            // Initializing public addresses.
            var phaKeys = PhantasmaKeys.FromWIF(wif);
            account.phaAddress = phaKeys.Address.ToString();

            var neoKeys = NeoKeys.FromWIF(wif);
            account.neoAddress = neoKeys.Address.ToString();

            var ethereumAddressUtil = new Phantasma.Ethereum.Util.AddressUtil();
            account.ethAddress = ethereumAddressUtil.ConvertToChecksumAddress(EthereumKey.FromWIF(wif).Address);

            if (!String.IsNullOrEmpty(password))
            {
                account.passwordProtected = true;
                account.passwordIterations = PasswordIterations;

                // Encrypting WIF.
                GetPasswordHash(password, account.passwordIterations, out string salt, out string passwordHash);
                account.password = "";
                account.salt = salt;

                account.WIF = EncryptString(wif, passwordHash, out string iv);
                account.iv = iv;

                // Decrypting to ensure there are no exceptions.
                DecryptString(account.WIF, passwordHash, account.iv);
            }
            else
            {
                account.passwordProtected = false;
                account.WIF = wif;
            }

            Accounts.Add(account);

            return Accounts.Count() - 1;
        }

        public static Address EncodeNeoAddress(string addressText)
        {
            Throw.If(!Phantasma.Neo.Utils.NeoUtils.IsValidAddress(addressText), "invalid neo address");
            var scriptHash = addressText.Base58CheckDecode();

            var pubKey = new byte[33];
            ByteArrayUtils.CopyBytes(scriptHash, 0, pubKey, 0, scriptHash.Length);

            return Address.FromInterop(1/*NeoID*/, pubKey);
        }

        public static Address EncodeEthereumAddress(string addressText)
        {
            var ethereumAddressUtil = new Phantasma.Ethereum.Util.AddressUtil();

            Throw.If(!ethereumAddressUtil.IsValidEthereumAddressHexFormat(addressText), "invalid Ethereum address");

            if (addressText.StartsWith("0x"))
            {
                addressText = addressText.Substring(2);
            }

            var scriptHash = Phantasma.Ethereum.Hex.HexConvertors.Extensions.HexByteConvertorExtensions.HexToByteArray(addressText);

            var pubKey = new byte[33];
            ByteArrayUtils.CopyBytes(scriptHash, 0, pubKey, 0, scriptHash.Length);

            return Address.FromInterop(2/*Ethereum*/, pubKey);
        }

        public static string DecodeNeoInteropAddress(Address address)
        {
            if (!address.IsInterop)
            {
                throw new Exception("not an interop address");
            }

            byte platformID;
            byte[] scriptHash;
            address.DecodeInterop(out platformID, out scriptHash);

            if (scriptHash[0] != 23)
            {
                throw new Exception("invalid NEO address");
            }

            scriptHash = scriptHash.Take(21).ToArray();

            return scriptHash.Base58CheckEncode();
        }

        private Dictionary<PlatformKind, string> _interopMap = new Dictionary<PlatformKind, string>();

        internal void FindInteropAddress(PlatformKind platform, Action<string> callback)
        {
            if (_interopMap.ContainsKey(platform))
            {
                callback(_interopMap[platform]);
                return;
            }

            StartCoroutine(phantasmaApi.GetPlatforms((platforms) =>
            {
                var platformName = platform.ToString().ToLower();
                foreach (var entry in platforms)
                {
                    if (entry.platform == platformName)
                    {
                        string interopAddress = entry.interop[0].external;
                        _interopMap[platform] = interopAddress;
                        Log.Write($"Got {interopAddress} interop address for {platformName} platform");
                        callback(interopAddress);
                        return;
                    }
                }

                callback(null);
            }, (error, msg) =>
            {
                if (error == EPHANTASMA_SDK_ERROR_TYPE.WEB_REQUEST_ERROR)
                {
                    ChangeFaultyRPCURL();
                }
                callback(null);
            }));
        }

        public string SearchInteropMapForAddress(PlatformKind platform)
        {
            if (_interopMap.ContainsKey(platform))
            {
                return _interopMap[platform];
            }

            return null;
        }

        public void ClearInteropMap()
        {
            _interopMap.Clear();
        }

        internal void SettleSwap(string sourcePlatform, string destPlatform, string symbol, string pendingHash, Action<Hash, string> callback)
        {
            if (sourcePlatform.ToLower() == PlatformKind.Ethereum.ToString().ToLower())
            {
                var wif = this.CurrentWif;
                var ethKeys = EthereumKey.FromWIF(wif);
                var phantasmaKeys = PhantasmaKeys.FromWIF(wif);

                Hash ethTxHash = Hash.Parse(pendingHash);
                var transcodedAddress = Address.FromKey(ethKeys);

                var script = ScriptUtils.BeginScript()
                    .CallContract("interop", "SettleTransaction", transcodedAddress, PlatformKind.Ethereum.ToString().ToLower(), PlatformKind.Ethereum.ToString().ToLower(), ethTxHash)
                    .CallContract("swap", "SwapFee", transcodedAddress, symbol, UnitConversion.ToBigInteger(0.1m, DomainSettings.FuelTokenDecimals))
                    .AllowGas(transcodedAddress, Address.Null, Settings.feePrice, Settings.feeLimit)
                    .TransferBalance(symbol, transcodedAddress, phantasmaKeys.Address)
                    .SpendGas(transcodedAddress)
                    .EndScript();

                SignAndSendTransaction("main", script, System.Text.Encoding.UTF8.GetBytes(WalletIdentifier), ProofOfWork.None, ethKeys, (hash, error) =>
                {
                    callback(hash, error);
                }, (message, prikey, pubkey) =>
                {
                    return Phantasma.Neo.Utils.CryptoUtils.Sign(message, prikey, pubkey, Phantasma.Cryptography.ECC.ECDsaCurve.Secp256k1);
                });
            }
            else
            {
                StartCoroutine(phantasmaApi.SettleSwap(sourcePlatform, destPlatform, pendingHash, (hash) =>
                {
                    callback(Hash.Parse(hash), null);
                }, (error, msg) =>
                {
                    if (error == EPHANTASMA_SDK_ERROR_TYPE.WEB_REQUEST_ERROR)
                    {
                        ChangeFaultyRPCURL();
                    }
                    Log.WriteWarning(msg);
                    callback(Hash.Null, msg);
                }));
            }
        }

        internal void DeleteAccount(int currentIndex)
        {
            if (currentIndex<0 || currentIndex >= Accounts.Count())
            {
                return;
            }

            Accounts.RemoveAt(currentIndex);
            SaveAccounts();
        }

        internal void ReplaceAccountWIF(int currentIndex, string wif)
        {
            if (currentIndex < 0 || currentIndex >= Accounts.Count())
            {
                return;
            }

            var account = Accounts[currentIndex];
            account.WIF = wif;
            Accounts[currentIndex] = account;
            SaveAccounts();
        }

        public bool RenameAccount(string newName)
        {
            foreach (var account in Accounts)
            {
                if (account.name.Equals(newName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            var account2 = Accounts[CurrentIndex];
            account2.name = newName;
            Accounts[CurrentIndex] = account2;
            SaveAccounts();
            return true;
        }

        internal void ValidateAccountName(string name, Action<string> callback)
        {
            StartCoroutine(
                this.phantasmaApi.LookUpName(name, (address) =>
                {
                    callback(address);
                },
                (error, msg) =>
                {
                    if (error == EPHANTASMA_SDK_ERROR_TYPE.WEB_REQUEST_ERROR)
                    {
                        ChangeFaultyRPCURL();
                    }
                    callback(null);
                })
            );
        }

        public string GetAddress(int index, PlatformKind platform)
        {
            if (index < 0 || index >= Accounts.Count())
            {
                return null;
            }

            if (index == _selectedAccountIndex)
            {
                if (_states.ContainsKey(platform))
                {
                    return _states[platform].address;
                }
            }

            switch (platform)
            {
                case PlatformKind.Phantasma:
                    return Accounts[index].phaAddress;

                case PlatformKind.Neo:
                    return Accounts[index].neoAddress;

                case PlatformKind.Ethereum:
                    return Accounts[index].ethAddress;
            }

            return null;
        }

        public void ResetNftsSorting()
        {
            currentTtrsNftsSortMode = TtrsNftSortMode.None;
            currentNftsSortMode = NftSortMode.None;
            currentNftsSortDirection = SortDirection.None;
        }

        public void SortTtrsNfts(string symbol)
        {
            if (_nfts[CurrentPlatform] == null)
                return;

            if (!nftDescriptionsAreFullyLoaded) // We should not sort NFTs if there are no attributes available.
                return;

            if (symbol == "TTRS")
            {
                if (currentTtrsNftsSortMode == (TtrsNftSortMode)Settings.ttrsNftSortMode && (int)currentNftsSortDirection == Settings.nftSortDirection)
                    return; // Nothing changed, no need to sort again.

                switch ((TtrsNftSortMode)Settings.ttrsNftSortMode)
                {
                    case TtrsNftSortMode.Number_Date:
                        if (Settings.nftSortDirection == (int)SortDirection.Ascending)
                            _nfts[CurrentPlatform] = _nfts[CurrentPlatform].OrderBy(x => TtrsStore.GetNft(x.ID).Mint).ThenBy(x => TtrsStore.GetNft(x.ID).Timestamp).ToList();
                        else
                            _nfts[CurrentPlatform] = _nfts[CurrentPlatform].OrderByDescending(x => TtrsStore.GetNft(x.ID).Mint).ThenByDescending(x => TtrsStore.GetNft(x.ID).Timestamp).ToList();
                        break;
                    case TtrsNftSortMode.Date_Number:
                        if (Settings.nftSortDirection == (int)SortDirection.Ascending)
                            _nfts[CurrentPlatform] = _nfts[CurrentPlatform].OrderBy(x => TtrsStore.GetNft(x.ID).Timestamp).ThenBy(x => TtrsStore.GetNft(x.ID).Mint).ToList();
                        else
                            _nfts[CurrentPlatform] = _nfts[CurrentPlatform].OrderByDescending(x => TtrsStore.GetNft(x.ID).Timestamp).ThenByDescending(x => TtrsStore.GetNft(x.ID).Mint).ToList();
                        break;
                    case TtrsNftSortMode.Type_Number_Date:
                        if (Settings.nftSortDirection == (int)SortDirection.Ascending)
                            _nfts[CurrentPlatform] = _nfts[CurrentPlatform].OrderByDescending(x => TtrsStore.GetNft(x.ID).Type).ThenBy(x => TtrsStore.GetNft(x.ID).Mint).ThenBy(x => TtrsStore.GetNft(x.ID).Timestamp).ToList();
                        else
                            _nfts[CurrentPlatform] = _nfts[CurrentPlatform].OrderBy(x => TtrsStore.GetNft(x.ID).Type).ThenByDescending(x => TtrsStore.GetNft(x.ID).Mint).ThenByDescending(x => TtrsStore.GetNft(x.ID).Timestamp).ToList();
                        break;
                    case TtrsNftSortMode.Type_Date_Number:
                        if (Settings.nftSortDirection == (int)SortDirection.Ascending)
                            _nfts[CurrentPlatform] = _nfts[CurrentPlatform].OrderByDescending(x => TtrsStore.GetNft(x.ID).Type).ThenBy(x => TtrsStore.GetNft(x.ID).Timestamp).ThenBy(x => TtrsStore.GetNft(x.ID).Mint).ToList();
                        else
                            _nfts[CurrentPlatform] = _nfts[CurrentPlatform].OrderBy(x => TtrsStore.GetNft(x.ID).Type).ThenByDescending(x => TtrsStore.GetNft(x.ID).Timestamp).ThenByDescending(x => TtrsStore.GetNft(x.ID).Mint).ToList();
                        break;
                    case TtrsNftSortMode.Type_Rarity: // And also Number and Date as last sorting parameters.
                        if (Settings.nftSortDirection == (int)SortDirection.Ascending)
                            _nfts[CurrentPlatform] = _nfts[CurrentPlatform].OrderByDescending(x => TtrsStore.GetNft(x.ID).Type).ThenByDescending(x => TtrsStore.GetNft(x.ID).Rarity).ThenBy(x => TtrsStore.GetNft(x.ID).Mint).ThenBy(x => TtrsStore.GetNft(x.ID).Timestamp).ToList();
                        else
                            _nfts[CurrentPlatform] = _nfts[CurrentPlatform].OrderBy(x => TtrsStore.GetNft(x.ID).Type).ThenBy(x => TtrsStore.GetNft(x.ID).Rarity).ThenByDescending(x => TtrsStore.GetNft(x.ID).Mint).ThenByDescending(x => TtrsStore.GetNft(x.ID).Timestamp).ToList();
                        break;
                }

                currentTtrsNftsSortMode = (TtrsNftSortMode)Settings.ttrsNftSortMode;
            }
            else
            {
                if (currentNftsSortMode == (NftSortMode)Settings.nftSortMode && (int)currentNftsSortDirection == Settings.nftSortDirection)
                    return; // Nothing changed, no need to sort again.

                switch ((NftSortMode)Settings.nftSortMode)
                {
                    case NftSortMode.Name:
                        if (Settings.nftSortDirection == (int)SortDirection.Ascending)
                            _nfts[CurrentPlatform] = _nfts[CurrentPlatform].OrderBy(x => GetNft(x.ID).parsedRom.GetName()).ToList();
                        else
                            _nfts[CurrentPlatform] = _nfts[CurrentPlatform].OrderByDescending(x => GetNft(x.ID).parsedRom.GetName()).ToList();
                        break;
                    case NftSortMode.Number_Date:
                        if (Settings.nftSortDirection == (int)SortDirection.Ascending)
                            _nfts[CurrentPlatform] = _nfts[CurrentPlatform].OrderBy(x => GetNft(x.ID).mint).ThenBy(x => GetNft(x.ID).parsedRom.GetDate()).ToList();
                        else
                            _nfts[CurrentPlatform] = _nfts[CurrentPlatform].OrderByDescending(x => GetNft(x.ID).mint).ThenByDescending(x => GetNft(x.ID).parsedRom.GetDate()).ToList();
                        break;
                    case NftSortMode.Date_Number:
                        if (Settings.nftSortDirection == (int)SortDirection.Ascending)
                            _nfts[CurrentPlatform] = _nfts[CurrentPlatform].OrderBy(x => GetNft(x.ID).parsedRom.GetDate()).ThenBy(x => GetNft(x.ID).mint).ToList();
                        else
                            _nfts[CurrentPlatform] = _nfts[CurrentPlatform].OrderByDescending(x => GetNft(x.ID).parsedRom.GetDate()).ThenByDescending(x => GetNft(x.ID).mint).ToList();
                        break;
                }

                currentNftsSortMode = (NftSortMode)Settings.nftSortMode;
            }
            
            currentNftsSortDirection = (SortDirection)Settings.nftSortDirection;
        }

        public decimal CalculateRequireStakeForStorage(int totalSize)
        {
            var kilobytesPerStake = 39; // TODO this should be governance value obtained from chain
            var stakeAmount = (totalSize * UnitConversion.GetUnitValue(DomainSettings.StakingTokenDecimals))  / (kilobytesPerStake * 1024);
            return UnitConversion.ToDecimal(stakeAmount, DomainSettings.StakingTokenDecimals);
        }

        public TokenData GetNft(string id)
        {
            return _nfts[CurrentPlatform].Where(x => x.ID == id).FirstOrDefault();
        }
    }
}
