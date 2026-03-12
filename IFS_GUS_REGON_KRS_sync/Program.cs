using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Text.Json;
using System.Xml.Linq;
using Oracle.ManagedDataAccess.Client;
using ServiceReference;

namespace MykLinkGusIfsSync
{
	internal class Program
	{
		static async Task Main(string[] args)
		{
			try
			{
				var config = LoadConfig("config.json");

				Log(config.LogFile, "Start...");
				Log(config.LogFile, "Ładowanie NIP-ów z Oracle...");

				var nips = GetDistinctNips(config.OracleConnectionString);

				Log(config.LogFile, $"Pobrano {nips.Count} unikalnych NIP-ów.");
				Log(config.LogFile, "");

				foreach (var nip in nips.Take(5))
				{
					Log(config.LogFile, nip);
				}

				if (nips.Count > 20)
				{
					Log(config.LogFile, "");
					Log(config.LogFile, $"... i jeszcze {nips.Count - 20} kolejnych.");
				}

				Log(config.LogFile, "");
				Log(config.LogFile, "Ładowanie cache...");

				var cache = LoadCache(config.CacheFile);

				Log(config.LogFile, $"Cache entries: {cache.Count}");
				Log(config.LogFile, "");

				int fromCache = 0;
				int toQuery = 0;

				foreach (var nip in nips)
				{
					if (TryGetFreshCache(cache, nip, config.CacheTtlDays, out _))
						fromCache++;
					else
						toQuery++;
				}

				Log(config.LogFile, $"Z cache: {fromCache}");
				Log(config.LogFile, $"Do zapytania GUS: {toQuery}");

				if (!File.Exists(config.CacheFile))
				{
					SaveCache(config.CacheFile, cache);
					Log(config.LogFile, "Utworzono pusty plik cache.");
				}

				var limiter = new SimpleRateLimiter(
					config.RequestsPerHour,
					config.RequestsPerMinute,
					config.RequestsPerSecond);

				using var gus = new GusClient(config);

				int processed = 0;
				int found = 0;
				int notFound = 0;
				int errors = 0;
				int writtenRegon = 0;
				int writtenKrs = 0;

				// do testów możesz chwilowo zmienić na nips.Take(10)
				foreach (var nip in nips)
				{
					GusCacheEntry cacheEntry;

					if (TryGetFreshCache(cache, nip, config.CacheTtlDays, out cacheEntry!))
					{
						Log(config.LogFile, $"CACHE | {nip} | {cacheEntry.Status}");
					}
					else
					{
						await limiter.WaitAsync();

						var gusResult = await gus.SearchByNipAsync(nip);

						cacheEntry = new GusCacheEntry
						{
							Nip = nip,
							Status = gusResult.Status,
							Regon = gusResult.Regon,
							Krs = gusResult.Krs,
							Name = gusResult.Name,
							CheckedAt = DateTime.Now,
							ErrorMessage = gusResult.ErrorMessage
						};

						cache[nip] = cacheEntry;

						Log(config.LogFile,
							$"GUS | {nip} | {cacheEntry.Status} | REGON={cacheEntry.Regon ?? "-"} | KRS={cacheEntry.Krs ?? "-"} | NAME={cacheEntry.Name ?? "-"} | ERR={cacheEntry.ErrorMessage ?? "-"}");

						if (cacheEntry.Status == "FOUND")
							found++;
						else if (cacheEntry.Status == "NOT_FOUND")
							notFound++;
						else if (cacheEntry.Status == "ERROR")
							errors++;
					}

					processed++;

					if (!string.Equals(cacheEntry.Status, "FOUND", StringComparison.OrdinalIgnoreCase))
					{
						if (processed % config.SaveCacheEvery == 0)
							SaveCache(config.CacheFile, cache);

						continue;
					}

					if (string.IsNullOrWhiteSpace(cacheEntry.Regon) && string.IsNullOrWhiteSpace(cacheEntry.Krs))
					{
						if (processed % config.SaveCacheEvery == 0)
							SaveCache(config.CacheFile, cache);

						continue;
					}

					var rows = GetIfsRowsByNip(config.OracleConnectionString, nip);

					if (rows.Count == 0)
					{
						Log(config.LogFile, $"IFS | {nip} | brak rekordów do wpisania");
					}
					else
					{
						foreach (var row in rows)
						{
							if (string.IsNullOrWhiteSpace(row.ExistingRegon) && !string.IsNullOrWhiteSpace(cacheEntry.Regon))
							{
								InsertIfsCode(
									config.OracleConnectionString,
									row.Company,
									row.Identity,
									row.PartyType,
									"REGON",
									cacheEntry.Regon!);

								writtenRegon++;
								Log(config.LogFile, $"WRITE_REGON | {nip} | {row.Company} | {row.Identity} | {cacheEntry.Regon}");
							}

							if (string.IsNullOrWhiteSpace(row.ExistingKrs) && !string.IsNullOrWhiteSpace(cacheEntry.Krs))
							{
								InsertIfsCode(
									config.OracleConnectionString,
									row.Company,
									row.Identity,
									row.PartyType,
									"KRS",
									cacheEntry.Krs!);

								writtenKrs++;
								Log(config.LogFile, $"WRITE_KRS | {nip} | {row.Company} | {row.Identity} | {cacheEntry.Krs}");
							}
						}
					}

					if (processed % config.SaveCacheEvery == 0)
					{
						SaveCache(config.CacheFile, cache);
						Log(config.LogFile, $"Zapisano cache po {processed} NIP-ach...");
					}
				}

				SaveCache(config.CacheFile, cache);

				Log(config.LogFile, "");
				Log(config.LogFile, "Koniec.");
				Log(config.LogFile, $"Przetworzone NIP-y: {processed}");
				Log(config.LogFile, $"FOUND: {found}");
				Log(config.LogFile, $"NOT_FOUND: {notFound}");
				Log(config.LogFile, $"ERROR: {errors}");
				Log(config.LogFile, $"Wpisane REGON: {writtenRegon}");
				Log(config.LogFile, $"Wpisane KRS: {writtenKrs}");
			}
			catch (Exception ex)
			{
				Console.WriteLine("BŁĄD:");
				Console.WriteLine(ex.ToString());
			}

			Console.WriteLine();
			Console.WriteLine("Naciśnij dowolny klawisz...");
			Console.ReadKey();
		}

		static Config LoadConfig(string path)
		{
			if (!File.Exists(path))
				throw new FileNotFoundException($"Nie znaleziono pliku konfiguracyjnego: {path}");

			var json = File.ReadAllText(path);
			var config = JsonSerializer.Deserialize<Config>(json, new JsonSerializerOptions
			{
				PropertyNameCaseInsensitive = true
			});

			if (config == null)
				throw new Exception("Nie udało się odczytać config.json");

			if (string.IsNullOrWhiteSpace(config.OracleConnectionString))
				throw new Exception("Brak OracleConnectionString w config.json");

			if (string.IsNullOrWhiteSpace(config.GusApiKey))
				throw new Exception("Brak GusApiKey w config.json");

			return config;
		}

		static void Log(string logFile, string message)
		{
			var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {message}";
			Console.WriteLine(message);
			File.AppendAllText(logFile, line + Environment.NewLine);
		}

		static Dictionary<string, GusCacheEntry> LoadCache(string path)
		{
			if (!File.Exists(path))
				return new Dictionary<string, GusCacheEntry>();

			var json = File.ReadAllText(path);

			return JsonSerializer.Deserialize<Dictionary<string, GusCacheEntry>>(json)
				   ?? new Dictionary<string, GusCacheEntry>();
		}

		static void SaveCache(string path, Dictionary<string, GusCacheEntry> cache)
		{
			var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions
			{
				WriteIndented = true
			});

			File.WriteAllText(path, json);
		}

		static bool TryGetFreshCache(
			Dictionary<string, GusCacheEntry> cache,
			string nip,
			int ttlDays,
			out GusCacheEntry entry)
		{
			if (cache.TryGetValue(nip, out entry!))
			{
				if (entry.CheckedAt >= DateTime.Now.AddDays(-ttlDays))
					return true;
			}

			entry = null!;
			return false;
		}

		static List<string> GetDistinctNips(string connectionString)
		{
			var result = new List<string>();

			const string sql = @"
with base as (
    select distinct
           case
             when a.party_type_db = 'CUSTOMER'
               then ifsapp.customer_info_api.Get_Association_No(a.identity)
             when a.party_type_db = 'SUPPLIER'
               then ifsapp.supplier_info_api.Get_Association_No(a.identity)
             else null
           end as nip_raw
      from ifsapp.IDENTITY_INVOICE_INFO a
      left join ifsapp.PARTY_TYPE_ID_PROPERTY regon
        on regon.identity = a.identity
       and regon.company = a.company
       and regon.party_type_db = a.party_type_db
       and regon.code = 'REGON'
      left join ifsapp.PARTY_TYPE_ID_PROPERTY krs
        on krs.identity = a.identity
       and krs.company = a.company
       and krs.party_type_db = a.party_type_db
       and krs.code = 'KRS'
     where krs.value is null
        or regon.value is null
),
normalized as (
    select case
             when upper(replace(replace(trim(nip_raw), '-', ''), ' ', '')) like 'PL%'
               then substr(upper(replace(replace(trim(nip_raw), '-', ''), ' ', '')), 3)
             else upper(replace(replace(trim(nip_raw), '-', ''), ' ', ''))
           end as nip
      from base
)
select distinct nip
  from normalized
 where regexp_like(nip, '^[0-9]{10}$')
   and mod(
         6 * to_number(substr(nip,1,1)) +
         5 * to_number(substr(nip,2,1)) +
         7 * to_number(substr(nip,3,1)) +
         2 * to_number(substr(nip,4,1)) +
         3 * to_number(substr(nip,5,1)) +
         4 * to_number(substr(nip,6,1)) +
         5 * to_number(substr(nip,7,1)) +
         6 * to_number(substr(nip,8,1)) +
         7 * to_number(substr(nip,9,1))
       , 11) <> 10
   and mod(
         6 * to_number(substr(nip,1,1)) +
         5 * to_number(substr(nip,2,1)) +
         7 * to_number(substr(nip,3,1)) +
         2 * to_number(substr(nip,4,1)) +
         3 * to_number(substr(nip,5,1)) +
         4 * to_number(substr(nip,6,1)) +
         5 * to_number(substr(nip,7,1)) +
         6 * to_number(substr(nip,8,1)) +
         7 * to_number(substr(nip,9,1))
       , 11) = to_number(substr(nip,10,1))
 order by nip";

			using var connection = new OracleConnection(connectionString);
			connection.Open();

			using var command = new OracleCommand(sql, connection);
			command.BindByName = true;

			using var reader = command.ExecuteReader();
			while (reader.Read())
			{
				if (!reader.IsDBNull(0))
				{
					var nip = reader.GetString(0).Trim();
					if (!string.IsNullOrWhiteSpace(nip))
						result.Add(nip);
				}
			}

			return result;
		}

		static List<IfsRow> GetIfsRowsByNip(string connectionString, string nip)
		{
			var result = new List<IfsRow>();

			const string sql = @"
with base as (
    select distinct
           a.company,
           a.identity,
           a.party_type,
           a.party_type_db,
           case
             when a.party_type_db = 'CUSTOMER'
               then ifsapp.customer_info_api.Get_Association_No(a.identity)
             when a.party_type_db = 'SUPPLIER'
               then ifsapp.supplier_info_api.Get_Association_No(a.identity)
             else null
           end as nip_raw,
           regon.value as regon,
           krs.value as krs
      from ifsapp.IDENTITY_INVOICE_INFO a
      left join ifsapp.PARTY_TYPE_ID_PROPERTY regon
        on regon.identity = a.identity
       and regon.company = a.company
       and regon.party_type_db = a.party_type_db
       and regon.code = 'REGON'
      left join ifsapp.PARTY_TYPE_ID_PROPERTY krs
        on krs.identity = a.identity
       and krs.company = a.company
       and krs.party_type_db = a.party_type_db
       and krs.code = 'KRS'
),
normalized as (
    select company,
           identity,
           party_type,
           party_type_db,
           regon,
           krs,
           case
             when upper(replace(replace(trim(nip_raw), '-', ''), ' ', '')) like 'PL%'
               then substr(upper(replace(replace(trim(nip_raw), '-', ''), ' ', '')), 3)
             else upper(replace(replace(trim(nip_raw), '-', ''), ' ', ''))
           end as nip
      from base
)
select company,
       identity,
       party_type,
       party_type_db,
       nip,
       regon,
       krs
  from normalized
 where nip = :p_nip
   and (regon is null or krs is null)
 order by company, identity";

			using var connection = new OracleConnection(connectionString);
			connection.Open();

			using var command = new OracleCommand(sql, connection);
			command.BindByName = true;
			command.Parameters.Add("p_nip", OracleDbType.Varchar2).Value = nip;

			using var reader = command.ExecuteReader();
			while (reader.Read())
			{
				var row = new IfsRow
				{
					Company = reader.IsDBNull(0) ? "" : reader.GetString(0),
					Identity = reader.IsDBNull(1) ? "" : reader.GetString(1),
					PartyType = reader.IsDBNull(2) ? "" : reader.GetString(2),
					PartyTypeDb = reader.IsDBNull(3) ? "" : reader.GetString(3),
					Nip = reader.IsDBNull(4) ? "" : reader.GetString(4),
					ExistingRegon = reader.IsDBNull(5) ? null : reader.GetString(5),
					ExistingKrs = reader.IsDBNull(6) ? null : reader.GetString(6)
				};

				result.Add(row);
			}

			return result;
		}

		static void InsertIfsCode(
			string connectionString,
			string company,
			string identity,
			string partyType,
			string code,
			string value)
		{
			const string plsql = @"
begin
  declare
    p0_ varchar2(32000) := null;
    p1_ varchar2(32000) := null;
    p2_ varchar2(32000) := null;
    p3_ varchar2(32000);
    p4_ varchar2(32000) := 'DO';
  begin
    p3_ := 'COMPANY' || chr(31) || :p_company ||
           chr(30) || 'IDENTITY' || chr(31) || :p_identity ||
           chr(30) || 'PARTY_TYPE' || chr(31) || :p_party_type ||
           chr(30) || 'CODE' || chr(31) || :p_code ||
           chr(30) || 'VALUE' || chr(31) || :p_value || chr(30);

    ifsapp.language_sys.set_language('pl');
    ifsapp.party_type_id_property_api.new__(p0_, p1_, p2_, p3_, p4_);
  end;
end;";

			using var connection = new OracleConnection(connectionString);
			connection.Open();

			using var command = new OracleCommand(plsql, connection);
			command.BindByName = true;

			command.Parameters.Add("p_company", OracleDbType.Varchar2).Value = company;
			command.Parameters.Add("p_identity", OracleDbType.Varchar2).Value = identity;
			command.Parameters.Add("p_party_type", OracleDbType.Varchar2).Value = partyType;
			command.Parameters.Add("p_code", OracleDbType.Varchar2).Value = code;
			command.Parameters.Add("p_value", OracleDbType.Varchar2).Value = value;

			command.ExecuteNonQuery();
		}
	}

	internal class GusClient : IDisposable
	{
		private readonly Config _config;
		private readonly UslugaBIRzewnPublClient _client;
		private string _sessionId;

		public GusClient(Config config)
		{
			_config = config;
			_client = new UslugaBIRzewnPublClient();
		}

		public async Task<GusResult> SearchByNipAsync(string nip)
		{
			try
			{
				if (string.IsNullOrWhiteSpace(_sessionId))
				{
					var loginResponse = await _client.ZalogujAsync(_config.GusApiKey);
					_sessionId = loginResponse.ZalogujResult;
				}

				using (new OperationContextScope((IContextChannel)_client.InnerChannel))
				{
					var httpRequest = new HttpRequestMessageProperty();
					httpRequest.Headers["sid"] = _sessionId;

					OperationContext.Current.OutgoingMessageProperties[HttpRequestMessageProperty.Name] = httpRequest;

					var searchResponse = await _client.DaneSzukajPodmiotyAsync(new ParametryWyszukiwania
					{
						Nip = nip
					});

					var raw = searchResponse.DaneSzukajPodmiotyResult;

					return ParseResult(raw, nip);
				}
			}
			catch (Exception ex)
			{
				return new GusResult
				{
					Nip = nip,
					Status = "ERROR",
					ErrorMessage = ex.Message
				};
			}
		}

		public void Dispose()
		{
			try
			{
				if (!string.IsNullOrWhiteSpace(_sessionId))
				{
					_client.WylogujAsync(_sessionId).GetAwaiter().GetResult();
				}
			}
			catch
			{
			}

			try
			{
				if (_client.State != CommunicationState.Closed)
					_client.Close();
			}
			catch
			{
				_client.Abort();
			}
		}

		private static GusResult ParseResult(string raw, string nip)
		{
			if (string.IsNullOrWhiteSpace(raw))
			{
				return new GusResult
				{
					Nip = nip,
					Status = "NOT_FOUND"
				};
			}

			var text = raw.Trim();

			if (!text.Contains("<"))
			{
				return new GusResult
				{
					Nip = nip,
					Status = "NOT_FOUND",
					ErrorMessage = text
				};
			}

			var doc = XDocument.Parse(text);

			var dane = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "dane");

			if (dane == null)
			{
				return new GusResult
				{
					Nip = nip,
					Status = "NOT_FOUND"
				};
			}

			var regon = GetValue(dane, "Regon");
			var krs = GetValue(dane, "Krs");
			var name = GetValue(dane, "Nazwa");

			return new GusResult
			{
				Nip = nip,
				Status = "FOUND",
				Regon = Clean(regon),
				Krs = Clean(krs),
				Name = Clean(name)
			};
		}

		private static string GetValue(XElement parent, string name)
		{
			return parent.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value;
		}

		private static string Clean(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
				return null;

			return value.Trim();
		}
	}

	internal class SimpleRateLimiter
	{
		private readonly int _perHour;
		private readonly int _perMinute;
		private readonly int _perSecond;

		private readonly Queue<DateTime> _hourQueue = new();
		private readonly Queue<DateTime> _minuteQueue = new();
		private readonly Queue<DateTime> _secondQueue = new();

		public SimpleRateLimiter(int perHour, int perMinute, int perSecond)
		{
			_perHour = perHour;
			_perMinute = perMinute;
			_perSecond = perSecond;
		}

		public async Task WaitAsync()
		{
			while (true)
			{
				var now = DateTime.UtcNow;

				Clean(_hourQueue, now.AddHours(-1));
				Clean(_minuteQueue, now.AddMinutes(-1));
				Clean(_secondQueue, now.AddSeconds(-1));

				if (_hourQueue.Count < _perHour &&
					_minuteQueue.Count < _perMinute &&
					_secondQueue.Count < _perSecond)
				{
					_hourQueue.Enqueue(now);
					_minuteQueue.Enqueue(now);
					_secondQueue.Enqueue(now);
					return;
				}

				await Task.Delay(400);
			}
		}

		private static void Clean(Queue<DateTime> queue, DateTime threshold)
		{
			while (queue.Count > 0 && queue.Peek() < threshold)
				queue.Dequeue();
		}
	}

	internal class Config
	{
		public string OracleConnectionString { get; set; } = "";
		public string GusApiKey { get; set; } = "";
		public string CacheFile { get; set; } = "gus-cache.json";
		public string LogFile { get; set; } = "gus-sync.log";
		public int CacheTtlDays { get; set; } = 30;
		public int SaveCacheEvery { get; set; } = 20;
		public int RequestsPerSecond { get; set; } = 2;
		public int RequestsPerMinute { get; set; } = 100;
		public int RequestsPerHour { get; set; } = 5000;
	}

	internal class GusCacheEntry
	{
		public string Nip { get; set; } = "";
		public string Status { get; set; } = "";
		public string Regon { get; set; }
		public string Krs { get; set; }
		public string Name { get; set; }
		public DateTime CheckedAt { get; set; }
		public string ErrorMessage { get; set; }
	}

	internal class GusResult
	{
		public string Nip { get; set; } = "";
		public string Status { get; set; } = "";
		public string Regon { get; set; }
		public string Krs { get; set; }
		public string Name { get; set; }
		public string ErrorMessage { get; set; }
	}

	internal class IfsRow
	{
		public string Company { get; set; } = "";
		public string Identity { get; set; } = "";
		public string PartyType { get; set; } = "";
		public string PartyTypeDb { get; set; } = "";
		public string Nip { get; set; } = "";
		public string ExistingRegon { get; set; }
		public string ExistingKrs { get; set; }
	}
}
