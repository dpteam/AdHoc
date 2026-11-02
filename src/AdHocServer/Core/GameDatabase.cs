using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Dpt.AdHoc.Core
{
    /// <summary>
    /// Product-ID -> game title database with cross-region links.
    ///
    /// Seeded from the PPSSPP / PRO Online default table, then user extendable through
    /// the web UI (persisted as data/games.json). No SQLite, no NuGet, no XML serializer.
    /// </summary>
    public sealed class GameDatabase
    {
        private readonly object _sync = new object();
        private readonly string _path;
        private Dictionary<string, string> _names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private DateTime _loadedWriteUtc = DateTime.MinValue;
        private DateTime _lastReloadCheckUtc = DateTime.MinValue;
        private bool _dirty;

        public GameDatabase(string dataDir)
        {
            try { Directory.CreateDirectory(dataDir); } catch { }
            _path = Path.Combine(dataDir, "games.json");
            Load();
        }

        public string FilePath { get { return _path; } }

        public int Count { get { lock (_sync) { return _names.Count; } } }
        public int CrosslinkCount { get { lock (_sync) { return _links.Count; } } }

        private void Load()
        {
            Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool loaded = false;

            if (File.Exists(_path))
            {
                try
                {
                    string text = File.ReadAllText(_path, Encoding.UTF8);
                    Dictionary<string, object> root = Json.ParseObject(text);
                    object g = Json.Get(root, "games");
                    object c = Json.Get(root, "crosslinks");
                    ReadMap(g, names);
                    ReadMap(c, links);
                    loaded = true;
                }
                catch (Exception ex)
                {
                    Log.Error("DB", "games.json is broken, falling back to built-in table: " + ex.Message);
                    try
                    {
                        string bak = _path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                        File.Copy(_path, bak, true);
                        Log.Warn("DB", "Broken database copied to " + bak);
                    }
                    catch { }
                }
            }

            lock (_sync)
            {
                _names = names;
                _links = links;
                if (!loaded)
                {
                    SeedDefaults();
                    _dirty = true;
                }
                try { _loadedWriteUtc = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue; }
                catch { _loadedWriteUtc = DateTime.MinValue; }
            }

            if (_dirty) Save();

            Log.Info("DB", "Game database ready: " + Count.ToString(CultureInfo.InvariantCulture) + " titles, " +
                            CrosslinkCount.ToString(CultureInfo.InvariantCulture) + " crosslinks");
        }

        private void ReadMap(object node, Dictionary<string, string> into)
        {
            Dictionary<string, object> map = node as Dictionary<string, object>;
            if (map == null) return;
            foreach (KeyValuePair<string, object> kv in map)
            {
                string key = NormalizeId(kv.Key);
                string val = kv.Value as string;
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(val)) continue;
                into[key] = Util.Clean(val, 160);
            }
        }

        /// <summary>Force a reload from disk (used by the console and /api/reload).</summary>
        public void Reload()
        {
            Load();
        }

        /// <summary>Reload if the file changed on disk (throttled to once per 2 seconds).</summary>
        public void MaybeReload()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastReloadCheckUtc).TotalSeconds < 2) return;
            _lastReloadCheckUtc = now;
            try
            {
                if (!File.Exists(_path)) return;
                DateTime write = File.GetLastWriteTimeUtc(_path);
                if (write == _loadedWriteUtc) return;
                Log.Info("DB", "games.json changed on disk, reloading");
                Load();
            }
            catch (Exception ex)
            {
                Log.Warn("DB", "reload failed: " + ex.Message);
            }
        }

        public void Save()
        {
            lock (_sync)
            {
                try
                {
                    // Pretty, stable, diff friendly output: one product id per line.
                    StringBuilder sb = new StringBuilder(16384);
                    sb.Append("{\n");
                    sb.Append("  \"version\": 1,\n");
                    sb.Append("  \"updatedUtc\": ").Append(Json.Quote(Util.Iso(DateTime.UtcNow))).Append(",\n");

                    List<string> keys = new List<string>(_names.Keys);
                    keys.Sort(StringComparer.OrdinalIgnoreCase);
                    sb.Append("  \"games\": {\n");
                    for (int i = 0; i < keys.Count; i++)
                    {
                        sb.Append("    ").Append(Json.Quote(keys[i])).Append(": ").Append(Json.Quote(_names[keys[i]]));
                        sb.Append(i == keys.Count - 1 ? "\n" : ",\n");
                    }
                    sb.Append("  },\n");

                    List<string> lkeys = new List<string>(_links.Keys);
                    lkeys.Sort(StringComparer.OrdinalIgnoreCase);
                    sb.Append("  \"crosslinks\": {\n");
                    for (int i = 0; i < lkeys.Count; i++)
                    {
                        sb.Append("    ").Append(Json.Quote(lkeys[i])).Append(": ").Append(Json.Quote(_links[lkeys[i]]));
                        sb.Append(i == lkeys.Count - 1 ? "\n" : ",\n");
                    }
                    sb.Append("  }\n");
                    sb.Append("}\n");

                    string tmp = _path + ".tmp";
                    File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                    if (File.Exists(_path)) File.Delete(_path);
                    File.Move(tmp, _path);
                    _loadedWriteUtc = File.GetLastWriteTimeUtc(_path);
                    _dirty = false;
                }
                catch (Exception ex)
                {
                    Log.Error("DB", "Save failed: " + ex.Message);
                }
            }
        }

        /// <summary>Follows a crosslink (single hop, cycle safe).</summary>
        public string Resolve(string productId)
        {
            string id = NormalizeId(productId);
            if (id.Length == 0) return id;
            lock (_sync)
            {
                string to;
                if (_links.TryGetValue(id, out to) && !string.IsNullOrEmpty(to) &&
                    !string.Equals(to, id, StringComparison.OrdinalIgnoreCase))
                    return NormalizeId(to);
                return id;
            }
        }

        public string NameOf(string productId)
        {
            string id = NormalizeId(productId);
            if (id.Length == 0) return "";
            lock (_sync)
            {
                string name;
                if (_names.TryGetValue(id, out name) && name.Length > 0) return name;
                string linked;
                if (_links.TryGetValue(id, out linked))
                {
                    string target = NormalizeId(linked);
                    if (_names.TryGetValue(target, out name) && name.Length > 0) return name;
                }
            }
            return "";
        }

        public bool HasTitle(string productId)
        {
            return NameOf(productId).Length > 0;
        }

        public string DisplayName(string productId)
        {
            string id = NormalizeId(productId);
            string name = NameOf(id);
            return name.Length > 0 ? name : id;
        }

        public string CrosslinkOf(string productId)
        {
            string id = NormalizeId(productId);
            lock (_sync)
            {
                string to;
                return _links.TryGetValue(id, out to) ? to : "";
            }
        }

        public void SetName(string productId, string name)
        {
            string id = NormalizeId(productId);
            if (id.Length == 0) return;
            lock (_sync)
            {
                _names[id] = Util.Clean(name, 160);
                _dirty = true;
            }
            Save();
        }

        public void SetCrosslink(string from, string to)
        {
            string a = NormalizeId(from);
            if (a.Length == 0) return;
            string b = NormalizeId(to);
            lock (_sync)
            {
                if (b.Length == 0) _links.Remove(a);
                else _links[a] = b;
                _dirty = true;
            }
            Save();
        }

        public List<KeyValuePair<string, string>> Snapshot()
        {
            lock (_sync)
            {
                List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>(_names.Count);
                foreach (KeyValuePair<string, string> kv in _names) list.Add(kv);
                list.Sort(delegate (KeyValuePair<string, string> x, KeyValuePair<string, string> y)
                {
                    return string.Compare(x.Value, y.Value, StringComparison.OrdinalIgnoreCase);
                });
                return list;
            }
        }

        public Dictionary<string, string> SnapshotLinks()
        {
            lock (_sync) { return new Dictionary<string, string>(_links, StringComparer.OrdinalIgnoreCase); }
        }

        public static string NormalizeId(string productId)
        {
            if (string.IsNullOrEmpty(productId)) return "";
            StringBuilder sb = new StringBuilder(9);
            for (int i = 0; i < productId.Length && sb.Length < 9; i++)
            {
                char c = char.ToUpperInvariant(productId[i]);
                if (c == '\0' || c == ' ') break;
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) sb.Append(c);
            }
            return sb.ToString();
        }

        private void SeedDefaults()
        {
            _names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            SeedTable(_names, DefaultGames);
            SeedTable(_links, DefaultCrosslinks);
        }

        private static void SeedTable(Dictionary<string, string> into, string[] rows)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                string row = rows[i];
                if (string.IsNullOrEmpty(row)) continue;
                int sep = row.IndexOf('|');
                if (sep <= 0) continue;
                string id = NormalizeId(row.Substring(0, sep));
                string val = row.Substring(sep + 1).Trim();
                if (id.Length == 0 || val.Length == 0) continue;
                into[id] = val;
            }
        }

        /// <summary>
        /// Default title table (from PPSSPP / PRO Online, GPLv3 source of PRO Online).
        /// Format: PRODUCTID|Title
        /// </summary>
        private static readonly string[] DefaultGames = new string[]
        {
            "ULUS10511|Ace Combat X2 - Joint Assault",
            "ULUS10245|Alien Syndrome",
            "NPUH10023|Armored Core 3 Portable",
            "ULES00719|Asphalt - Urban GT 2",
            "ULUS10579|BlazBlue - Continuum Shift 2",
            "ULUS10519|BlazBlue Calamity Trigger",
            "UCJS10110|Bleach Heat The Soul 7",
            "ULUS10516|Blood Bowl",
            "ULUS10121|Bomberman",
            "ULUS10319|Bomberman Land",
            "ULES00703|Burnout Dominator",
            "ULES00125|Burnout Legends",
            "ULJM05538|Busou Shinki - Battle Masters",
            "ULUS10057|Bust A Move Deluxe",
            "ULUS10218|Call of Duty - Roads to Victory",
            "ULUS10351|Code Lyoko - Quest for Infinity",
            "NPJH50583|Conception - Please have my children!",
            "ULUS10044|Crash Tag Team Racing",
            "ULUS10100|Def Jam Fight For NY - The Takeover",
            "NPJH50588|Digimon World Re:Digitize",
            "ULUS10566|Dissidia 012 Duodecim Final Fantasy",
            "ULUS10437|Dissidia Final Fantasy",
            "ULUS10081|Dragon Ball Z - Shin Budokai",
            "ULUS10234|Dragon Ball Z - Shin Budokai 2",
            "ULUS10537|Dragon Ball Z - Tenkaichi Tag Team",
            "ULJS00311|Dragon Ball Z - Tenkaichi Tag Team",
            "NPJH90135|Dragon Ball Z - Tenkaichi Tag Team",
            "ULJM05127|Dragon Quest & Final Fantasy in Itadaki Street Special",
            "ULES00847|Dungeon Explorer - Warriors of Ancient Arts",
            "ULUS10177|Dungeon Siege - Throne of Agony",
            "ULUS10170|Dynasty Warrior 2",
            "ULES01221|Dynasty Warriors - Strike Force",
            "ULUS10416|Dynasty Warriors - Strike Force",
            "UCUS98701|Everybody's Tennis",
            "UCUS98740|Fat Princess - Fistful of Cake",
            "ULJM05360|Fate Tiger Colosseum Upper",
            "ULUS10297|Final Fantasy Tactics - The War of the Lions",
            "ULES00850|Final Fantasy Tactics - War of the Lions",
            "NPJH50443|Final Fantasy Type 0",
            "NPJH50468|Frontier Gate",
            "NPJH50721|Frontier Gate Boost+",
            "ULES01432|Full Metal Alchemist - Brotherhood",
            "ULUS10490|GTA Chinatown Wars",
            "ULUS10160|GTA Vice City Stories",
            "ULUS10041|GTA Liberty City Stories",
            "ULES00151|GTA Liberty City Stories",
            "ULUS10210|Ghost Rider",
            "ULJS00237|God Eater",
            "NPJH50832|God Eater 2",
            "ULUS10563|God Eater Burst",
            "UCUS98632|Gran Turismo",
            "NPJH50107|Gundam VS Gundam - Next Plus",
            "ULJM05933|Hatsune Miku - Project Diva Extend",
            "ULUS10298|Hot Pixel",
            "ULJM05709|K-ON! Houkago Live",
            "NPJH50221|Kateikyoushi Hitman Reborn! Kizuna no Tag Battle",
            "ULJS00165|Kidou Senshi Gundam - Gundam vs. Gundam",
            "UCUS98646|Killzone Liberation",
            "ULJM05775|Kingdom Hearts - Birth by Sleep Final Mix",
            "ULUS10487|LEGO Indiana Jones 2",
            "NPJH50503|Lord of Apocalypse",
            "ULES01507|Lord of Arcana",
            "ULUS10180|M.A.C.H. - Modified Air Combat Heroes",
            "UCUS98758|MLB11 - The Show",
            "ULUS10581|Madden NFL 12",
            "ULJS00385|Mahou Shoujo Nanoha A's Portable - The Gears of Destiny",
            "ULUS10408|Mana Khemia Student Alliance",
            "ULUS10141|Medal Of Honor Heroes",
            "NPJH50045|Metal Gear Solid - Peace Walker",
            "ULUS10202|Metal Gear Solid - Portable Ops",
            "ULUS10290|Metal Gear Solid - Portable Ops +",
            "ULUS10154|Metal Slug Anthology",
            "ULUS10495|Metal Slug XX",
            "ULES01429|Metal Slug XX",
            "ULES00368|Micro Machines V4",
            "ULUS10383|Midnight Club - LA Remix",
            "UCUS98741|Mod Nation Racers",
            "ULUS10084|Monster Hunter Freedom",
            "ULUS10266|Monster Hunter Freedom 2",
            "ULUS10391|Monster Hunter Freedom Unite",
            "ULJM05800|Monster Hunter Portable 3rd",
            "ULJM06097|Musou Orochi 2 Special",
            "ULUS10340|N+",
            "ULES01578|NBA 2K13",
            "ULUS10598|NBA 2K13",
            "ULUS10349|Naruto - Ultimate Ninja Heroes 2",
            "ULUS10518|Naruto - Ultimate Ninja Heroes 3",
            "ULJS00236|Naruto - Accel 3",
            "ULUS10582|Naruto Shippuden - Ultimate Ninja Impact",
            "ULES01537|Naruto Shippuden - Ultimate Ninja Impact",
            "ULUS10571|Naruto Shippuden - Kizuna Drive",
            "ULES00196|Need For Speed - Most Wanted",
            "ULUS10036|Need For Speed - Most Wanted",
            "ULUS10376|Need for Speed - Undercover",
            "ULKS46004|Need for Speed - Underground Rivals",
            "ULES01340|Obscure - The Aftermath",
            "ULUS10064|Outrun 2006 - Coast 2 Coast",
            "ULUS10586|PRO Evolution Soccer 2012",
            "ULUS10149|Pac Man - World Rally",
            "ULUS10438|Pangya! - Fantasy Golf",
            "UCUS98732|Patapon 2",
            "UCUS98751|Patapon 3",
            "ULUS10410|Phantasy Star Portable",
            "ULUS10529|Phantasy Star Portable 2",
            "NPJH50332|Phantasy Star Portable 2",
            "ULJM05732|Phantasy Star Portable 2 - Infinity",
            "ULES01596|Pro Evolution Soccer 2014",
            "ULES01595|Pro Evolution Soccer 2015",
            "NPJH50520|Pro Yakyuu Spirits 2012",
            "NPJH50838|Pro Yakyuu Spirits 2014",
            "NPJH50492|Puyo Puyo!! 20th Anniversary",
            "ULUS10292|Renegrade Squadron",
            "UCJS10090|Resistance - Retribution",
            "ULES00670|Rocky Balboa",
            "ULJS00360|Rurouni Kenshin - Meiji Kenkaku Romantan Saisen",
            "UCUS98615|SOCOM - Fireteam Bravo",
            "UCUS98645|SOCOM - Fireteam Bravo 2",
            "UCUS98716|SOCOM - Fireteam Bravo 3",
            "NPJH50460|Sengoku Basara - Chronicles Heroes",
            "ULJM05436|Sengoku Basara - Battle Heroes",
            "ULJM05637|Shin Sangoku Musou - Multi Raid 2",
            "ULJM05035|Shinobido - Tales of the Ninja",
            "ULUS10194|Shrek - Smash and Crash Racing",
            "UCES00758|Smash Court Tennis 3",
            "ULUS10195|Sonic Rivals",
            "ULUS10457|Soul Calibur - Broken Destiny",
            "ULUS10513|Split Second - Velocity",
            "ULES00183|Star Wars Battle Front 2",
            "ULUS10062|Street Fighter Alpha 3 MAX",
            "NPUH10020|Strikers 1945 Plus Portable",
            "ULUS10548|TRON - Evolution",
            "NPJH50426|Taiko no Tatsujin Portable DX",
            "ULUS10466|Tekken 6",
            "NPJH50691|Tokusatsu University",
            "ULUS10445|Tom Clancy's Ghost Recon - Predator",
            "ULES01350|Tom Clancy's Ghost Recon - Predator",
            "NPJH50789|Toukiden",
            "NPJH50878|Toukiden - Kiwami",
            "UCUS98601|Twisted Metal - Head On",
            "ULUS10508|UFC Undisputed 2010",
            "ULJS00069|Ultraman Fighting Evo Zero",
            "ULUS10003|Untold Legends - Brotherhood of the Blade",
            "ULUS10086|Untold Legends - The Warrior's Code",
            "ULUS10515|Valkryia Chronicles 2",
            "ULUS10087|Viewtiful Joe",
            "ULUS10246|Virtua Tennis 3",
            "ULUS82741|WWE 2K14",
            "ULUS10543|WWE Smackdown vs. Raw 2011",
            "ULUS10423|Warriors Orochi 2",
            "ULJM05553|Warship Gunner 2 Portable",
            "ULJS00155|Way Of The Samurai",
            "UCES00465|Wipeout Pulse",
            "ULUS10321|World Series of Poker 2008 - Battle for the Bracelets",
            "NPUH10045|Worms Battle Islands",
            "ULUS10065|Worms Open Warfare",
            "ULUS10260|Worms Open Warfare 2",
            "ULJM05734|Yu-Gi-Oh! 5D's Tag Force 5",
            "ULJM05940|Yu-Gi-Oh! 5D's Tag Force 6",
            "NPJH00142|Yu-Gi-Oh! Arc-V Tag Force",
            "ULJM05151|Yu-Gi-Oh! GX Tag Force",
            "ULJM05373|Yu-Gi-Oh! GX Tag Force 3",
            "NPUG80086|flOw"
        };

        /// <summary>Default cross-region links. Format: FROM|TO</summary>
        private static readonly string[] DefaultCrosslinks = new string[]
        {
            "ULES01408|ULUS10511",
            "NPJH50263|ULUS10511",
            "ULJM05492|NPUH10023",
            "NPJH50401|ULUS10579",
            "ULES01230|ULUS10516",
            "ULJM05034|ULUS10121",
            "ULES00469|ULUS10121",
            "ULJM05316|ULUS10121",
            "ULJM05181|ULUS10319",
            "ULJM05319|ULUS10319",
            "ULES00959|ULUS10319",
            "ULES00643|ULUS10218",
            "ULES01505|ULUS10566",
            "NPJH50377|ULUS10566",
            "ULES01270|ULUS10437",
            "ULJM05262|ULUS10437",
            "ULJS00049|ULUS10081",
            "ULKS46085|ULUS10081",
            "ULES00309|ULUS10081",
            "ULJS00107|ULUS10234",
            "ULES00789|ULUS10234",
            "ULES01456|ULUS10537",
            "ULES00569|ULUS10177",
            "UCJS10101|UCUS98701",
            "UCES01420|UCUS98701",
            "UCES01312|UCUS98740",
            "NPHG00025|UCUS98740",
            "ULES01519|ULUS10563",
            "NPJH50352|ULUS10563",
            "UCES01245|UCUS98632",
            "UCES00543|UCUS98645",
            "ULJS00250|NPJH50107",
            "ULJS19048|NPJH50107",
            "NPJH50465|ULJM05933",
            "ULES00642|ULUS10298",
            "ULJM05767|ULES01507",
            "ULUS10479|ULES01507",
            "ULES00565|ULUS10180",
            "ULES00566|ULUS10180",
            "ULJM05202|ULUS10180",
            "ULES01372|NPJH50045",
            "ULUS10509|NPJH50045",
            "ULES00645|ULUS10202",
            "ULJM05193|ULUS10202",
            "ULES01003|ULUS10290",
            "ULJM05261|ULUS10290",
            "ULES01144|ULUS10383",
            "ULJS00180|ULUS10383",
            "UCES01327|UCUS98741",
            "UCJS10112|UCUS98741",
            "UCAS40306|UCUS98741",
            "ULJM05066|ULUS10084",
            "ULES00318|ULUS10084",
            "ULJM05156|ULUS10266",
            "ULES00851|ULUS10266",
            "ULES01213|ULUS10391",
            "ULJM05500|ULUS10391",
            "ULES01026|ULUS10340",
            "ULJM05403|ULUS10376",
            "ULJM05612|ULUS10376",
            "ULES01145|ULUS10376",
            "ULES00262|ULUS10064",
            "ULJM05440|ULUS10438",
            "ULKS46164|ULUS10438",
            "ULES01540|ULUS10586",
            "ULES01541|ULUS10586",
            "ULES01542|ULUS10586",
            "ULAS42289|ULUS10586",
            "UCJS10089|UCUS98732",
            "PSPJ30000|UCUS98732",
            "UCES01177|UCUS98732",
            "UCJS18036|UCUS98732",
            "UCES01421|UCUS98751",
            "NPJG00122|UCUS98751",
            "ULJM05309|ULUS10410",
            "ULES01218|ULUS10410",
            "ULJM08023|ULUS10410",
            "ULJM05493|ULUS10529",
            "ULJM08030|ULUS10529",
            "ULES01439|ULUS10529",
            "UCES01184|UCJS10090",
            "UCUS98668|UCJS10090",
            "ULUS10233|ULES00670",
            "UCES00038|UCUS98615",
            "UCJS10102|UCUS98615",
            "UCES01242|UCUS98716",
            "NPJG00035|UCUS98716",
            "ULES00618|ULUS10194",
            "ULJS00098|UCES00758",
            "ULUS10269|UCES00758",
            "ULES01298|ULUS10457",
            "ULJS00202|ULUS10457",
            "ULES01402|ULUS10513",
            "ULJM05812|ULUS10513",
            "ULJM05082|ULUS10062",
            "ULES00235|ULUS10062",
            "ULJM05225|ULUS10062",
            "ULJS00383|NPJH50426",
            "ULES01376|ULUS10466",
            "NPJH50184|ULUS10466",
            "ULJS00224|ULUS10466",
            "ULES01495|ULUS10548",
            "ULES00046|ULUS10003",
            "ULJM05087|ULUS10003",
            "ULKS46015|ULUS10003",
            "ULES00301|ULUS10086",
            "ULJM05179|ULUS10086",
            "ULKS46069|ULUS10086",
            "ULES00763|ULUS10246",
            "ULES00991|ULUS10321",
            "NPEH00019|NPUH10045",
            "ULES00268|ULUS10065",
            "ULES00819|ULUS10260",
            "ULUS10555|ULJM05734",
            "ULES01474|ULJM05734"
        };
    }
}