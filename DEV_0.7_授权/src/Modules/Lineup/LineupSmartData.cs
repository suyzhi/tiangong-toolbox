using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;
namespace TianGongCadSuite {
    public static class LineupPresets {
        public static readonly string[] Homes={"缩回","伸出","松开","关闭","非真空"};
        public static readonly string[] CylinderMountings={"水平安装","垂直朝上","垂直朝下","竖直向上","竖直向下"};
        public static readonly string[] ValveKinds={"阀岛","汇流排","阀组","单片阀"};
        public static readonly string[] Connectors={"M8/3针","M8/4针","M12/4针","螺丝端子 M20 x 1.5","2 m 导线引出型","3线式","散线/3针"};
        public static readonly string[] ValveDescriptions={"三位五通阀，中封式","三位五通中泄阀","二位五通阀，双电控","二位五通阀，单电控","二位三通阀","盖板"};
    }
    public sealed class LineupIsland {
        public string Key="",Model="",Kind="阀岛";
        public LineupIsland Copy(){return new LineupIsland{Key=Key??"",Model=Model??"",Kind=string.IsNullOrWhiteSpace(Kind)?"阀岛":Kind};}
    }
    public static class LineupNumbering {
        public static string Next(LineupProject p,string category){
            string prefix;
            if(category=="气缸"||category=="阀片"){
                if(string.IsNullOrWhiteSpace(p.NumberPrefix)||p.ActiveIsland<1)throw new InvalidOperationException("请先在“编号与阀岛”设置项目号和阀岛数字。");
                prefix=p.NumberPrefix.Trim()+"-Y"+p.ActiveIsland+(category=="气缸"?"C":"V");
            }else if(category=="普通传感器")prefix="SE";else if(category=="其他电气件")prefix="E";
            else throw new InvalidOperationException("气缸传感器请从气缸生成 H/W；辅助类目保留手动编号。");
            var pattern=new Regex("^"+Regex.Escape(prefix)+"([0-9]+)(?:-[0-9]+|[a-z])?$",RegexOptions.IgnoreCase);int max=0;
            foreach(var r in p.Records){var m=pattern.Match((r.Number??"").Trim());int value;if(m.Success&&int.TryParse(m.Groups[1].Value,out value))max=Math.Max(max,value);}
            if(max==int.MaxValue)throw new InvalidOperationException("编号已耗尽。");
            return prefix+(max+1).ToString("D2");
        }
        public static string IslandKey(string number){
            var m=Regex.Match((number??"").Trim(),@"^(.+-Y[0-9]+)[CV][0-9]+(?:-[0-9]+)?[a-z]?(?:[HWM][0-9]*)?$",RegexOptions.IgnoreCase);
            return m.Success?m.Groups[1].Value:"";
        }
        public static LineupIsland IslandEntry(LineupProject p,string key){
            if(string.IsNullOrEmpty(key)||p==null||p.Islands==null)return null;
            return p.Islands.FirstOrDefault(i=>string.Equals(i.Key,key,StringComparison.OrdinalIgnoreCase));
        }
        public static string IslandModel(LineupProject p,LineupRecord r){
            if(r.Category!="阀片")return "";
            var island=IslandEntry(p,IslandKey(r.Number));
            return island==null?"":(island.Model??"");
        }
        public static string IslandLabel(LineupProject p,LineupRecord r){
            if(r.Category!="阀片")return "";
            var island=IslandEntry(p,IslandKey(r.Number));
            if(island==null)return "";
            string kind=string.IsNullOrWhiteSpace(island.Kind)?"阀岛":island.Kind.Trim();
            string model=(island.Model??"").Trim();
            if(kind=="单片阀")return model.Length==0?"单片阀":"单片阀："+model;
            return model.Length==0?"":kind+"："+model;
        }
        public static void SetIsland(LineupProject p,string prefix,int island,string model){
            SetIsland(p,prefix,island,model,null);
        }
        public static void SetIsland(LineupProject p,string prefix,int island,string model,string kind){
            prefix=(prefix??"").Trim();if(prefix.Length==0||prefix.IndexOfAny(new[]{'\r','\n','\t'})>=0||island<1)throw new InvalidOperationException("项目号不能为空，阀岛数字必须大于 0。");
            p.NumberPrefix=prefix;p.ActiveIsland=island;if(p.Islands==null)p.Islands=new List<LineupIsland>();
            string key=prefix+"-Y"+island;var entry=p.Islands.FirstOrDefault(x=>string.Equals(x.Key,key,StringComparison.OrdinalIgnoreCase));
            if(entry==null){entry=new LineupIsland{Key=key,Kind="阀岛"};p.Islands.Add(entry);}
            entry.Model=model??"";
            if(!string.IsNullOrWhiteSpace(kind))entry.Kind=kind.Trim();
            else if(string.IsNullOrWhiteSpace(entry.Kind))entry.Kind="阀岛";
        }
        // 根据气缸编号推导配套阀片编号：OK170-Y1C05-1 → OK170-Y1V05，OK270-Y2C02a → OK270-Y2V02
        public static string ValveNumberForCylinder(string cylinderNumber){
            var m=Regex.Match((cylinderNumber??"").Trim(),@"^(.+-Y[0-9]+)C([0-9]+)(?:-[0-9]+)?[a-z]?$",RegexOptions.IgnoreCase);
            return m.Success?m.Groups[1].Value+"V"+m.Groups[2].Value:"";
        }
        // 从所选气缸一键生成对应阀片并建立关联；同组 -1/-2 气缸共用同一阀片，阀片功能参数自动带入气缸描述
        public static int GenerateValves(LineupProject p,IEnumerable<string> cylinderIds){
            int added=0;
            var cylinders=cylinderIds.Distinct().Select(id=>p.Records.FirstOrDefault(r=>r.Id==id&&r.Category=="气缸")).Where(r=>r!=null).ToList();
            if(cylinders.Count==0)throw new InvalidOperationException("请先选择已编号的气缸。");
            foreach(var group in cylinders.GroupBy(c=>ValveNumberForCylinder(c.Number),StringComparer.OrdinalIgnoreCase)){
                string valveNum=group.Key;
                if(string.IsNullOrEmpty(valveNum))continue;
                var first=group.First();
                string ikey=IslandKey(valveNum);
                LineupRecord islandRec=null;
                if(ikey.Length>0){
                    islandRec=p.Records.FirstOrDefault(r=>r.Category=="阀岛"&&string.Equals(r.Number,ikey,StringComparison.OrdinalIgnoreCase));
                    if(islandRec==null){
                        var cfg=IslandEntry(p,ikey);
                        if(cfg!=null&&!string.IsNullOrWhiteSpace(cfg.Model)){islandRec=new LineupRecord{Category="阀岛",Number=ikey,Model=cfg.Model};p.Records.Add(islandRec);}
                    }
                }
                var valve=p.Records.FirstOrDefault(r=>r.Category=="阀片"&&string.Equals((r.Number??"").Trim(),valveNum,StringComparison.OrdinalIgnoreCase));
                if(valve==null){
                    if(p.Records.Any(r=>string.Equals((r.Number??"").Trim(),valveNum,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("编号已被其他类别占用："+valveNum);
                    var preset=p.EntryPresets==null?null:p.EntryPresets.LastOrDefault(x=>x.Category=="阀片");
                    string func=string.Join(" / ",group.Select(c=>(c.Description??"").Trim()).Where(s=>s.Length>0).Distinct());
                    valve=new LineupRecord{
                        Number=valveNum,Category="阀片",Parameters=func,
                        Description=preset!=null&&!string.IsNullOrWhiteSpace(preset.Description)?preset.Description:"三位五通阀，中封式",
                        Model=preset!=null?preset.Model:"",Brand=preset!=null?preset.Brand:"",
                        Mounting=preset!=null?preset.Mounting:"",
                        Mechanism=first.Mechanism,Notes=first.Notes,
                        IslandId=islandRec==null?"":islandRec.Id
                    };
                    p.Records.Add(valve);added++;
                }else{
                    if(string.IsNullOrWhiteSpace(valve.Parameters)&&!string.IsNullOrWhiteSpace(first.Description))valve.Parameters=first.Description;
                    if(string.IsNullOrWhiteSpace(valve.Notes)&&!string.IsNullOrWhiteSpace(first.Notes))valve.Notes=first.Notes;
                }
                foreach(var cyl in group){
                    cyl.ValveId=valve.Id;
                    if(!string.IsNullOrEmpty(valve.IslandId))cyl.IslandId=valve.IslandId;
                    foreach(var sen in p.Records.Where(r=>r.Category=="气缸传感器"&&r.CylinderId==cyl.Id)){
                        sen.ValveId=valve.Id;if(!string.IsNullOrEmpty(valve.IslandId))sen.IslandId=valve.IslandId;
                    }
                }
            }
            return added;
        }
        public static int SensorPairs(LineupProject p,IEnumerable<string> cylinderIds){
            return SensorPairsMulti(p,cylinderIds,1);
        }
        // pairCount=1 → H / W；pairCount=2 → H1 / W1 / H2 / W2（线体大行程提升气缸常用双组传感器）
        public static int SensorPairsMulti(LineupProject p,IEnumerable<string> cylinderIds,int pairCount){
            if(pairCount<1||pairCount>4)throw new ArgumentOutOfRangeException("pairCount");
            int added=0;foreach(string id in cylinderIds.Distinct()){
                var cylinder=p.Records.FirstOrDefault(r=>r.Id==id&&r.Category=="气缸");
                if(cylinder==null||string.IsNullOrWhiteSpace(cylinder.Number))throw new InvalidOperationException("请先选择已编号的气缸。");
                var suffixes=new List<KeyValuePair<string,string>>();
                if(pairCount==1){suffixes.Add(new KeyValuePair<string,string>("H","原位"));suffixes.Add(new KeyValuePair<string,string>("W","工作位"));}
                else for(int k=1;k<=pairCount;k++){suffixes.Add(new KeyValuePair<string,string>("H"+k,"原位"+k));suffixes.Add(new KeyValuePair<string,string>("W"+k,"工作位"+k));}
                foreach(var pair in suffixes){
                    string position=pair.Key,label=pair.Value;
                    string number=cylinder.Number.Trim()+position;
                    if(p.Records.Any(r=>r.CylinderId==id&&string.Equals(r.Position,position,StringComparison.OrdinalIgnoreCase)))continue;
                    if(p.Records.Any(r=>string.Equals((r.Number??"").Trim(),number,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("编号已被其他记录占用："+number);
                    p.Records.Add(new LineupRecord{
                        Number=number,Category="气缸传感器",CylinderId=id,ValveId=cylinder.ValveId,IslandId=cylinder.IslandId,
                        Position=position,Description=(cylinder.Description??"")+label,Mechanism=cylinder.Mechanism,Notes=cylinder.Notes
                    });
                    added++;
                }
            }return added;
        }
        public static void FillMissing(LineupProject p,IEnumerable<LineupRecord> records){
            foreach(var r in records.Where(r=>string.IsNullOrWhiteSpace(r.Number))){
                if(r.Category=="气缸传感器"){
                    var parent=p.Records.FirstOrDefault(x=>x.Id==r.CylinderId&&x.Category=="气缸");
                    if(parent!=null&&!string.IsNullOrWhiteSpace(parent.Number)&&!string.IsNullOrWhiteSpace(r.Position))r.Number=parent.Number+r.Position;
                }else if(LineupTableData.Categories.Contains(r.Category))r.Number=Next(p,r.Category);
            }LineupTableData.RejectDuplicates(p.Records);
        }

        // ==================== 删除后自动顺延重排编号（阀岛 Y* / 阀片 V** / 气缸 C** / 磁开 H/W / SE** / E**） ====================
        sealed class SlotBucket {
            public readonly List<LineupRecord> Valves = new List<LineupRecord>();
            public readonly List<KeyValuePair<LineupRecord, string>> Cylinders = new List<KeyValuePair<LineupRecord, string>>();
        }

        // 同步 CAD 位置图页上的标注编号：移除已删记录对应的点位，并把已重排记录的新编号同步到点位
        public static void SyncCadPageNumbers(LineupProject p, HashSet<string> deletedIds = null) {
            if (p == null || p.CadPages == null || p.CadPages.Count == 0) return;
            var byId = new Dictionary<string, LineupRecord>(StringComparer.Ordinal);
            if (p.Records != null) {
                foreach (var r in p.Records) if (!string.IsNullOrEmpty(r.Id) && !byId.ContainsKey(r.Id)) byId[r.Id] = r;
            }
            foreach (var pg in p.CadPages) {
                if (pg == null || pg.Points == null) continue;
                if (deletedIds != null && deletedIds.Count > 0)
                    pg.Points.RemoveAll(pt => pt != null && !string.IsNullOrEmpty(pt.Id) && deletedIds.Contains(pt.Id));
                foreach (var pt in pg.Points) {
                    if (pt == null || string.IsNullOrEmpty(pt.Id)) continue;
                    LineupRecord rec;
                    if (byId.TryGetValue(pt.Id, out rec) && !string.IsNullOrWhiteSpace(rec.Number))
                        pt.Number = rec.Number.Trim();
                }
            }
        }

        // 对指定阀岛（如 OK170-Y1）内的槽位编号从 01 起连续重排，同步级联同槽阀片 V**、气缸 C**（含 -1/-2/a 后缀）及 H/W 磁开
        public static int CompactIslandSlots(LineupProject p, string islandKey) {
            if (p == null || p.Records == null || string.IsNullOrWhiteSpace(islandKey)) return 0;
            string key = islandKey.Trim();
            var valvePat = new Regex("^(" + Regex.Escape(key) + ")V([0-9]+)$", RegexOptions.IgnoreCase);
            var cylPat = new Regex("^(" + Regex.Escape(key) + ")C([0-9]+)(-[0-9]+|[a-z])?$", RegexOptions.IgnoreCase);
            var senSuffixPat = new Regex(@"([HWM][0-9]*)$", RegexOptions.IgnoreCase);

            var matchedValves = new Dictionary<string, int>(StringComparer.Ordinal);
            var buckets = new SortedDictionary<int, SlotBucket>();
            string canonicalKey = key;

            Func<int, SlotBucket> getBucket = slot => {
                SlotBucket b;
                if (!buckets.TryGetValue(slot, out b)) { b = new SlotBucket(); buckets[slot] = b; }
                return b;
            };

            foreach (var r in p.Records) {
                if (LineupTableData.Category(r.Category) != "阀片") continue;
                var m = valvePat.Match((r.Number ?? "").Trim());
                int slot;
                if (!m.Success || !int.TryParse(m.Groups[2].Value, out slot) || slot < 1) continue;
                if (buckets.Count == 0) canonicalKey = m.Groups[1].Value;
                matchedValves[r.Id] = slot;
                getBucket(slot).Valves.Add(r);
            }

            var matchedCyls = new List<LineupRecord>();
            var cylByNum = new Dictionary<string, LineupRecord>(StringComparer.OrdinalIgnoreCase);
            var cylById = new Dictionary<string, LineupRecord>(StringComparer.Ordinal);
            foreach (var r in p.Records) {
                if (LineupTableData.Category(r.Category) != "气缸") continue;
                string raw = (r.Number ?? "").Trim();
                var m = cylPat.Match(raw);
                int slot;
                if (!m.Success || !int.TryParse(m.Groups[2].Value, out slot) || slot < 1) continue;
                if (buckets.Count == 0 && matchedCyls.Count == 0) canonicalKey = m.Groups[1].Value;
                string suffix = m.Groups[3].Success ? m.Groups[3].Value : "";
                int valveSlot;
                if (!string.IsNullOrEmpty(r.ValveId) && matchedValves.TryGetValue(r.ValveId, out valveSlot)) slot = valveSlot;
                getBucket(slot).Cylinders.Add(new KeyValuePair<LineupRecord, string>(r, suffix));
                matchedCyls.Add(r);
                cylById[r.Id] = r;
                if (raw.Length > 0 && !cylByNum.ContainsKey(raw)) cylByNum[raw] = r;
            }

            if (buckets.Count == 0) return 0;

            // 先在改号前把所有关联气缸传感器绑定到对应气缸，防止改号过程中发生编号碰撞或漏改
            var sensorsByCyl = new Dictionary<string, List<KeyValuePair<LineupRecord, string>>>(StringComparer.Ordinal);
            var mappedSensors = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in p.Records) {
                if (LineupTableData.Category(s.Category) != "气缸传感器") continue;
                string sn = (s.Number ?? "").Trim();
                LineupRecord owner = null;
                string pos = (s.Position ?? "").Trim();
                if (!string.IsNullOrEmpty(s.CylinderId) && cylById.TryGetValue(s.CylinderId, out owner)) {
                    if (pos.Length == 0) {
                        var sm = senSuffixPat.Match(sn);
                        if (sm.Success) pos = sm.Groups[1].Value;
                    }
                } else if (sn.Length > 0) {
                    var sm = Regex.Match(sn, @"^(.+?)([HWM][0-9]*)$", RegexOptions.IgnoreCase);
                    if (sm.Success && cylByNum.TryGetValue(sm.Groups[1].Value.Trim(), out owner)) {
                        if (pos.Length == 0) pos = sm.Groups[2].Value;
                    }
                }
                if (owner != null && pos.Length > 0 && mappedSensors.Add(s.Id)) {
                    List<KeyValuePair<LineupRecord, string>> list;
                    if (!sensorsByCyl.TryGetValue(owner.Id, out list)) { list = new List<KeyValuePair<LineupRecord, string>>(); sensorsByCyl[owner.Id] = list; }
                    list.Add(new KeyValuePair<LineupRecord, string>(s, pos));
                }
            }

            int renamed = 0;
            int nextSlot = 1;
            foreach (var kv in buckets) {
                int oldSlot = kv.Key;
                var bucket = kv.Value;
                if (oldSlot != nextSlot) {
                    string slotStr = nextSlot.ToString("D2");
                    foreach (var v in bucket.Valves) {
                        string nextNum = canonicalKey + "V" + slotStr;
                        if (!string.Equals(v.Number, nextNum, StringComparison.Ordinal)) { v.Number = nextNum; renamed++; }
                    }
                    foreach (var pair in bucket.Cylinders) {
                        var c = pair.Key;
                        string nextNum = canonicalKey + "C" + slotStr + pair.Value;
                        if (!string.Equals(c.Number, nextNum, StringComparison.Ordinal)) { c.Number = nextNum; renamed++; }
                        List<KeyValuePair<LineupRecord, string>> slist;
                        if (sensorsByCyl.TryGetValue(c.Id, out slist)) {
                            foreach (var sp in slist) {
                                string nextSen = c.Number + sp.Value;
                                if (!string.Equals(sp.Key.Number, nextSen, StringComparison.Ordinal)) { sp.Key.Number = nextSen; renamed++; }
                            }
                        }
                    }
                }
                nextSlot++;
            }
            return renamed;
        }

        // 删除中间阀岛（如 Y2）后，同前缀的后续阀岛（Y3→Y2…）连同其配置、阀片、气缸、磁开与已同步 E** 一起顺延重排
        public static int CompactIslands(LineupProject p, string prefix) {
            if (p == null || string.IsNullOrWhiteSpace(prefix)) return 0;
            string pre = prefix.Trim();
            var keyPat = new Regex("^(" + Regex.Escape(pre) + ")-Y([0-9]+)$", RegexOptions.IgnoreCase);
            var nums = new SortedSet<int>();
            string canonicalPrefix = pre;
            Action<string> collect = raw => {
                var m = keyPat.Match((raw ?? "").Trim());
                int n;
                if (m.Success && int.TryParse(m.Groups[2].Value, out n) && n >= 1) {
                    if (nums.Count == 0) canonicalPrefix = m.Groups[1].Value;
                    nums.Add(n);
                }
            };
            if (p.Islands != null) foreach (var isl in p.Islands) collect(isl.Key);
            if (p.Records != null) {
                foreach (var r in p.Records) {
                    if (r.Category == "阀岛") collect(r.Number);
                    else {
                        string cat = LineupTableData.Category(r.Category);
                        if (cat == "阀片" || cat == "气缸") collect(IslandKey(r.Number));
                    }
                }
            }
            if (nums.Count == 0) return 0;

            var numMap = new Dictionary<int, int>();
            var keyMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int target = 1;
            foreach (int oldNum in nums) {
                if (oldNum != target) {
                    numMap[oldNum] = target;
                    keyMap[canonicalPrefix + "-Y" + oldNum] = canonicalPrefix + "-Y" + target;
                }
                target++;
            }
            if (keyMap.Count == 0) return 0;

            int renamed = 0;
            if (p.Islands != null) {
                foreach (var isl in p.Islands) {
                    string nk;
                    if (!string.IsNullOrWhiteSpace(isl.Key) && keyMap.TryGetValue(isl.Key.Trim(), out nk)) {
                        isl.Key = nk;
                        renamed++;
                    }
                }
            }
            var itemPat = new Regex("^(" + Regex.Escape(pre) + "-Y[0-9]+)([CV].*)$", RegexOptions.IgnoreCase);
            if (p.Records != null) {
                foreach (var r in p.Records) {
                    string raw = (r.Number ?? "").Trim();
                    string cat = LineupTableData.Category(r.Category);
                    if (r.Category == "阀岛") {
                        string nk;
                        if (keyMap.TryGetValue(raw, out nk)) {
                            r.Number = nk;
                            if (!string.IsNullOrEmpty(r.Description))
                                r.Description = Regex.Replace(r.Description, Regex.Escape(raw), nk, RegexOptions.IgnoreCase);
                            renamed++;
                        }
                    } else if (cat == "阀片" || cat == "气缸" || cat == "气缸传感器") {
                        var m = itemPat.Match(raw);
                        string nk;
                        if (m.Success && keyMap.TryGetValue(m.Groups[1].Value, out nk)) {
                            r.Number = nk + m.Groups[2].Value;
                            renamed++;
                        }
                    } else if (cat == "其他电气件") {
                        string note = (r.Notes ?? "").Trim();
                        string nk;
                        if (note.Length > 0 && keyMap.TryGetValue(note, out nk)) {
                            r.Notes = nk;
                            if (!string.IsNullOrEmpty(r.Description))
                                r.Description = Regex.Replace(r.Description, Regex.Escape(note), nk, RegexOptions.IgnoreCase);
                        }
                    }
                }
            }
            if (string.Equals((p.NumberPrefix ?? "").Trim(), canonicalPrefix, StringComparison.OrdinalIgnoreCase)) {
                int newActive;
                if (numMap.TryGetValue(p.ActiveIsland, out newActive)) p.ActiveIsland = newActive;
            }
            return renamed;
        }

        // 线性序列顺排：用于普通传感器 (SE01/SE02…) 与其他电气件 (E01/E02…)，跳过手工自定义编号
        public static int CompactLinearCategory(LineupProject p, string targetCategory) {
            if (p == null || p.Records == null) return 0;
            Regex pat;
            string token;
            if (targetCategory == "普通传感器") { pat = new Regex(@"^((?:.*-)?)SE([0-9]+)$", RegexOptions.IgnoreCase); token = "SE"; }
            else if (targetCategory == "其他电气件") { pat = new Regex(@"^((?:.*-)?)E([0-9]+)$", RegexOptions.IgnoreCase); token = "E"; }
            else return 0;

            var groups = new Dictionary<string, List<KeyValuePair<int, LineupRecord>>>(StringComparer.OrdinalIgnoreCase);
            var canonicalPrefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < p.Records.Count; i++) {
                var r = p.Records[i];
                if (LineupTableData.Category(r.Category) != targetCategory) continue;
                var m = pat.Match((r.Number ?? "").Trim());
                int n;
                if (!m.Success || !int.TryParse(m.Groups[2].Value, out n) || n < 1) continue;
                string pre = m.Groups[1].Value + token;
                List<KeyValuePair<int, LineupRecord>> list;
                if (!groups.TryGetValue(pre, out list)) {
                    list = new List<KeyValuePair<int, LineupRecord>>();
                    groups[pre] = list;
                    canonicalPrefixes[pre] = pre;
                }
                list.Add(new KeyValuePair<int, LineupRecord>(n, r));
            }

            int renamed = 0;
            foreach (var kv in groups) {
                string canonical = canonicalPrefixes[kv.Key];
                var ordered = kv.Value.Select((pair, idx) => new { Num = pair.Key, Idx = idx, Rec = pair.Value })
                    .OrderBy(x => x.Num).ThenBy(x => x.Idx).ToList();
                for (int i = 0; i < ordered.Count; i++) {
                    string nextNum = canonical + (i + 1).ToString("D2");
                    if (!string.Equals(ordered[i].Rec.Number, nextNum, StringComparison.Ordinal)) {
                        ordered[i].Rec.Number = nextNum;
                        renamed++;
                    }
                }
            }
            return renamed;
        }

        // 删除记录后自动检测受影响的编号序列并重排：覆盖 1阀多缸、同阀岛槽位、同前缀阀岛、SE**、E** 与 CAD 位置图点位
        public static int CompactAfterDelete(LineupProject p, IEnumerable<LineupRecord> removed, HashSet<string> removedIds = null) {
            if (p == null) return 0;
            var islands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var survivingValves = new HashSet<string>(StringComparer.Ordinal);
            var islandPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool compactSe = false, compactE = false;

            var islandRecNums = new Dictionary<string, string>(StringComparer.Ordinal);
            if (p.Records != null) {
                foreach (var r in p.Records) if (r.Category == "阀岛" && !string.IsNullOrWhiteSpace(r.Number)) islandRecNums[r.Id] = r.Number.Trim();
            }

            if (removed != null) {
                foreach (var r in removed) {
                    if (r == null) continue;
                    string cat = LineupTableData.Category(r.Category);
                    if (r.Category == "阀岛") {
                        var m = Regex.Match((r.Number ?? "").Trim(), @"^(.+)-Y([0-9]+)$", RegexOptions.IgnoreCase);
                        if (m.Success) islandPrefixes.Add(m.Groups[1].Value.Trim());
                    } else if (cat == "气缸" || cat == "阀片" || cat == "气缸传感器") {
                        string ik = IslandKey(r.Number);
                        if (ik.Length == 0 && !string.IsNullOrEmpty(r.IslandId)) islandRecNums.TryGetValue(r.IslandId, out ik);
                        if (!string.IsNullOrEmpty(ik)) islands.Add(ik);
                        if (cat == "气缸" && !string.IsNullOrEmpty(r.ValveId)) survivingValves.Add(r.ValveId);
                    } else if (cat == "普通传感器") {
                        compactSe = true;
                    } else if (cat == "其他电气件") {
                        compactE = true;
                    }
                }
            }

            int renamed = 0;
            if (p.Records != null) {
                foreach (string vid in survivingValves) {
                    var valve = p.Records.FirstOrDefault(r => r.Id == vid && LineupTableData.Category(r.Category) == "阀片");
                    if (valve != null) {
                        var before = p.Records.Where(r => r.ValveId == valve.Id).ToDictionary(r => r.Id, r => r.Number ?? "");
                        try { LineupHierarchy.RenumberChildren(p, valve); } catch { }
                        foreach (var kv in before) {
                            var cur = p.Records.FirstOrDefault(r => r.Id == kv.Key);
                            if (cur != null && !string.Equals(cur.Number ?? "", kv.Value, StringComparison.Ordinal)) renamed++;
                        }
                    }
                }
            }
            foreach (string ik in islands) renamed += CompactIslandSlots(p, ik);
            foreach (string pre in islandPrefixes) renamed += CompactIslands(p, pre);
            if (compactSe) renamed += CompactLinearCategory(p, "普通传感器");
            if (compactE) renamed += CompactLinearCategory(p, "其他电气件");
            SyncCadPageNumbers(p, removedIds);
            return renamed;
        }

        // 一键重新顺排整个项目的所有编号（方便清理历史项目遗留的跳号空洞）
        public static int CompactAll(LineupProject p) {
            if (p == null) return 0;
            int renamed = 0;
            var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> addKey = raw => {
                var m = Regex.Match((raw ?? "").Trim(), @"^(.+)-Y([0-9]+)$", RegexOptions.IgnoreCase);
                if (m.Success) { prefixes.Add(m.Groups[1].Value.Trim()); keys.Add(m.Value); }
            };
            if (p.Islands != null) foreach (var isl in p.Islands) addKey(isl.Key);
            if (p.Records != null) {
                foreach (var r in p.Records) {
                    if (r.Category == "阀岛") addKey(r.Number);
                    else addKey(IslandKey(r.Number));
                }
            }
            foreach (string pre in prefixes) renamed += CompactIslands(p, pre);
            keys.Clear();
            if (p.Islands != null) foreach (var isl in p.Islands) addKey(isl.Key);
            if (p.Records != null) foreach (var r in p.Records) addKey(r.Category == "阀岛" ? r.Number : IslandKey(r.Number));
            foreach (string ik in keys) renamed += CompactIslandSlots(p, ik);
            renamed += CompactLinearCategory(p, "普通传感器");
            renamed += CompactLinearCategory(p, "其他电气件");
            SyncCadPageNumbers(p, null);
            return renamed;
        }
    }
    public sealed class LineupModelEntry {
        public string Category="",SubCategory="",Model="",Brand="",Connector="",Notes="";
        public string EffectiveSubCategory {
            get {
                return !string.IsNullOrWhiteSpace(SubCategory)
                    ? SubCategory.Trim()
                    : LineupModelCatalog.InferSubCategory(Category,Model,Brand);
            }
        }
        public LineupModelEntry Copy(){
            return new LineupModelEntry{
                Category=Category??"",SubCategory=SubCategory??"",
                Model=Model??"",Brand=Brand??"",Connector=Connector??"",Notes=Notes??""
            };
        }
        public override string ToString(){return Model+" · "+Brand+(Connector.Length==0?"":" · "+Connector);}
    }
    public sealed class LineupModelCatalog {
        public const string SeedResource="TianGongCadSuite.Lineup.lineup-models-seed.xml";
        public static readonly string[] Categories={"气缸","阀片","阀岛","气缸传感器","普通传感器","其他电气件"};
        public bool SeedInitialized;
        public List<LineupModelEntry> Entries=new List<LineupModelEntry>();
        static readonly XmlSerializer CatalogSerializer=new XmlSerializer(typeof(LineupModelCatalog));
        public static string UserFile {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TianGongCadSuite","lineup-models.xml");}}
        public static bool IsSensorCategory(string category){return category=="气缸传感器"||category=="普通传感器";}
        public static int CategoryOrder(string category){int i=Array.IndexOf(Categories,category??"");return i>=0?i:99;}
        public static string[] DefaultSubCategories(string category){
            if(category=="气缸")return new[]{"薄型气缸","导杆气缸","标准/圆型气缸","气爪/夹爪","滑台气缸","锁紧/阻挡气缸","吸盘/真空","专用/集成气缸","通用气缸"};
            if(category=="阀片")return new[]{"电磁阀片","盖板/附件","真空发生器","其他阀片"};
            if(category=="阀岛")return new[]{"总线阀岛","汇流排","真空阀岛","单片阀/其他"};
            if(category=="气缸传感器")return new[]{"磁性开关","接近开关","其他磁开"};
            if(category=="普通传感器")return new[]{"电感接近开关","光电/激光传感器","行程/安全开关","磁性开关","其他传感器"};
            if(category=="其他电气件")return new[]{"视觉相机/镜头","安全光栅","工业机器人","电机/减速机","拧紧系统","RFID","气源处理/调压","其他电气件"};
            return new[]{"未分类"};
        }
        // 根据大类与型号前缀自动归入细分类型，避免同大类下几十种不同原理的型号混堆在一起
        public static string InferSubCategory(string category,string model,string brand){
            string m=(model??"").Trim().ToUpperInvariant();
            string b=(brand??"").Trim().ToUpperInvariant();
            if(category=="气缸"){
                if(m.StartsWith("CDQ2")||m.StartsWith("CQ2")||m.StartsWith("ADN")||m.StartsWith("SDA"))return "薄型气缸";
                if(m.StartsWith("MGP")||m.StartsWith("TCL")||m.StartsWith("TN"))return "导杆气缸";
                if(m.StartsWith("MXS")||m.StartsWith("MXQ")||m.StartsWith("HLQ")||m.StartsWith("CXS"))return "滑台气缸";
                if(m.StartsWith("MHC")||m.StartsWith("MHS")||m.StartsWith("MHZ")||m.StartsWith("MHF")||m.StartsWith("RH"))return "气爪/夹爪";
                if(m.StartsWith("CLQ")||m.StartsWith("RSDQ")||m.StartsWith("RSQ")||m.StartsWith("RS2H"))return "锁紧/阻挡气缸";
                if(m.StartsWith("ZP")||m.Contains("真空"))return "吸盘/真空";
                if(m.StartsWith("AR")||m.StartsWith("AC")||m.StartsWith("AW"))return "气源处理/调压";
                if(m.Contains("气缸"))return "专用/集成气缸";
                if(m.StartsWith("MB")||m.StartsWith("MDB")||m.StartsWith("CDM")||m.StartsWith("CDS")||m.StartsWith("DSNU")||m.StartsWith("DPDM")||m.StartsWith("HMF")||m.StartsWith("SC"))return "标准/圆型气缸";
                return "通用气缸";
            }
            if(category=="阀片"){
                if(m.StartsWith("SY50M")||m.StartsWith("SY70M")||m.Contains("-26-")||m.Contains("盖板"))return "盖板/附件";
                if(m.StartsWith("ZK2")||m.Contains("真空"))return "真空发生器";
                if(m.StartsWith("SY")||m.StartsWith("VFS")||m.StartsWith("VQ")||m.StartsWith("VUVG")||m.StartsWith("VF"))return "电磁阀片";
                return "其他阀片";
            }
            if(category=="阀岛"){
                if(m.StartsWith("ZK2")||m.StartsWith("ZZK"))return "真空阀岛";
                if(m.Contains("EX260")||m.Contains("SDAN")||m.StartsWith("SS5Y")||m.StartsWith("VTUG")||m.StartsWith("CPX"))return "总线阀岛";
                return "汇流排";
            }
            if(category=="气缸传感器"){
                if(m.StartsWith("D-M9")||m.StartsWith("D-Y5")||m.StartsWith("D-A9")||m.StartsWith("D-Z7")||m.StartsWith("SMT")||m.StartsWith("CS1"))return "磁性开关";
                if(m.StartsWith("E2B")||m.StartsWith("E2E")||m.StartsWith("IM"))return "接近开关";
                return "其他磁开";
            }
            if(category=="普通传感器"){
                if(m.StartsWith("E2B")||m.StartsWith("E2E")||m.StartsWith("TL-")||m.StartsWith("IM"))return "电感接近开关";
                if(m.StartsWith("E3Z")||m.StartsWith("E3AS")||m.StartsWith("E3S")||m.StartsWith("E3F")||m.StartsWith("LR-")||m.StartsWith("PZ-"))return "光电/激光传感器";
                if(m.StartsWith("WL")||m.StartsWith("D4")||m.StartsWith("T4VH")||m.StartsWith("AZ")||b.Contains("SCHMERSAL")||b.Contains("施迈赛"))return "行程/安全开关";
                if(m.StartsWith("D-M9")||m.StartsWith("D-Y5")||m.StartsWith("D-A9"))return "磁性开关";
                return "其他传感器";
            }
            if(category=="其他电气件"){
                if(m.StartsWith("MV-")||m.StartsWith("MVL-")||m.StartsWith("CV-")||b.Contains("海康"))return "视觉相机/镜头";
                if(m.StartsWith("F3SG")||m.StartsWith("GL-")||m.StartsWith("SF4"))return "安全光栅";
                if(m.StartsWith("R-1000")||m.StartsWith("R-2000")||b.Contains("发那科")||b.Contains("FANUC"))return "工业机器人";
                if(m.StartsWith("RNYM")||m.StartsWith("RNHM")||m.StartsWith("B30")||m.StartsWith("B40")||m.StartsWith("B63")||b.Contains("住友")||b.Contains("LAFERT"))return "电机/减速机";
                if(m.StartsWith("SF36")||m.StartsWith("SF812")||m.StartsWith("8435")||b.Contains("ATLAS")||b.Contains("阿特拉斯"))return "拧紧系统";
                if(m.StartsWith("V680"))return "RFID";
                if(m.StartsWith("AR")||m.StartsWith("AC")||m.StartsWith("AW")||m.Contains("气源"))return "气源处理/调压";
                return "其他电气件";
            }
            return "未分类";
        }
        public LineupModelCatalog Clone(){
            var c=new LineupModelCatalog{SeedInitialized=SeedInitialized};
            if(Entries!=null)foreach(var e in Entries)c.Entries.Add(e.Copy());
            return c;
        }
        public void EnsureSubCategories(){
            if(Entries==null)return;
            foreach(var e in Entries){
                if(string.IsNullOrWhiteSpace(e.SubCategory))e.SubCategory=InferSubCategory(e.Category,e.Model,e.Brand);
            }
        }
        public void SortEntries(){
            if(Entries==null)return;
            Entries=Entries
                .OrderBy(e=>CategoryOrder(e.Category))
                .ThenBy(e=>e.Category??"",StringComparer.OrdinalIgnoreCase)
                .ThenBy(e=>e.EffectiveSubCategory??"",StringComparer.OrdinalIgnoreCase)
                .ThenBy(e=>e.Brand??"",StringComparer.OrdinalIgnoreCase)
                .ThenBy(e=>e.Model??"",StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        public static List<string[]> ExportRows(IEnumerable<LineupModelEntry> entries){
            var rows=new List<string[]>{new[]{"类别","细分类型","型号","品牌","传感器接口类型","备注"}};
            if(entries==null)return rows;
            var sorted=entries
                .OrderBy(e=>CategoryOrder(e.Category))
                .ThenBy(e=>e.Category??"",StringComparer.OrdinalIgnoreCase)
                .ThenBy(e=>e.EffectiveSubCategory??"",StringComparer.OrdinalIgnoreCase)
                .ThenBy(e=>e.Brand??"",StringComparer.OrdinalIgnoreCase)
                .ThenBy(e=>e.Model??"",StringComparer.OrdinalIgnoreCase);
            foreach(var e in sorted)rows.Add(new[]{e.Category??"",e.EffectiveSubCategory??"",e.Model??"",e.Brand??"",e.Connector??"",e.Notes??""});
            return rows;
        }
        public void AddOrUpdate(LineupModelEntry existing,LineupModelEntry next){
            if(next==null)throw new ArgumentNullException("next");
            string cat=LineupTableData.Category(next.Category);
            if(!Categories.Contains(cat))throw new InvalidOperationException("请选择有效的器件大类。");
            string model=(next.Model??"").Trim();
            string brand=(next.Brand??"").Trim();
            string conn=(next.Connector??"").Trim();
            string sub=(next.SubCategory??"").Trim();
            string notes=(next.Notes??"").Trim();
            if(model.Length==0)throw new InvalidOperationException("型号不能为空。");
            if(brand.Length==0)throw new InvalidOperationException("品牌不能为空。");
            if(IsSensorCategory(cat)&&conn.Length==0)throw new InvalidOperationException("传感器类条目请填写传感器接口类型（如 M8/3针、M12/4针）。");
            if(sub.Length==0)sub=InferSubCategory(cat,model,brand);
            bool dup=Entries.Any(e=>!ReferenceEquals(e,existing)&&Same(e.Category,cat)&&Same(e.Model,model)&&Same(e.Brand,brand)&&Same(e.Connector,conn));
            if(dup)throw new InvalidOperationException("型号库中已存在完全相同的条目（大类/型号/品牌/接口一致）。");
            if(existing!=null){
                existing.Category=cat;existing.SubCategory=sub;existing.Model=model;existing.Brand=brand;existing.Connector=conn;existing.Notes=notes;
            }else{
                Entries.Add(new LineupModelEntry{Category=cat,SubCategory=sub,Model=model,Brand=brand,Connector=conn,Notes=notes});
            }
        }
        public int RemoveEntries(IEnumerable<LineupModelEntry> targets){
            if(targets==null)return 0;
            var set=new HashSet<LineupModelEntry>(targets);
            return Entries.RemoveAll(e=>set.Contains(e));
        }
        // 按 (大类, 型号, 品牌, 接口) 去重并合并非空细分类型/备注，消除内置种子或旧库重复条目导致的补全报错和无法编辑
        public int Deduplicate(){
            if(Entries==null||Entries.Count<=1)return 0;
            var kept=new List<LineupModelEntry>(Entries.Count);
            var map=new Dictionary<string,LineupModelEntry>(StringComparer.OrdinalIgnoreCase);
            int removed=0;
            foreach(var e in Entries){
                if(e==null){removed++;continue;}
                string cat=LineupTableData.Category(e.Category);
                string key=cat+"\u001F"+(e.Model??"").Trim()+"\u001F"+(e.Brand??"").Trim()+"\u001F"+(e.Connector??"").Trim();
                LineupModelEntry existing;
                if(map.TryGetValue(key,out existing)){
                    if(string.IsNullOrWhiteSpace(existing.SubCategory)&&!string.IsNullOrWhiteSpace(e.SubCategory))existing.SubCategory=e.SubCategory.Trim();
                    if(string.IsNullOrWhiteSpace(existing.Notes)&&!string.IsNullOrWhiteSpace(e.Notes))existing.Notes=e.Notes.Trim();
                    removed++;
                    continue;
                }
                e.Category=cat;
                map[key]=e;
                kept.Add(e);
            }
            if(removed>0)Entries=kept;
            return removed;
        }
        internal void SanitizeInPlace(){
            if(Entries==null){Entries=new List<LineupModelEntry>();return;}
            for(int i=0;i<Entries.Count;i++){
                var e=Entries[i];
                if(e==null)continue;
                e.Category=LineupXmlSanitizer.Clean(e.Category);
                e.SubCategory=LineupXmlSanitizer.Clean(e.SubCategory);
                e.Model=LineupXmlSanitizer.Clean(e.Model);
                e.Brand=LineupXmlSanitizer.Clean(e.Brand);
                e.Connector=LineupXmlSanitizer.Clean(e.Connector);
                e.Notes=LineupXmlSanitizer.Clean(e.Notes);
            }
        }
        public static LineupModelCatalog Load(string file){
            if(!File.Exists(file))return new LineupModelCatalog();
            var rs=new XmlReaderSettings{CheckCharacters=false,DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null};
            LineupModelCatalog c;
            using(var s=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
            using(var r=XmlReader.Create(s,rs))c=(LineupModelCatalog)CatalogSerializer.Deserialize(r);
            if(c==null)c=new LineupModelCatalog();
            c.SanitizeInPlace();
            c.Deduplicate();
            return c;
        }
        public static LineupModelCatalog LoadSeed(string diskPath){
            if(!string.IsNullOrEmpty(diskPath)&&File.Exists(diskPath))return Load(diskPath);
            using(var s=typeof(LineupModelCatalog).Assembly.GetManifestResourceStream(SeedResource)){
                if(s!=null){
                    var rs=new XmlReaderSettings{CheckCharacters=false,DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null};
                    LineupModelCatalog c;
                    using(var r=XmlReader.Create(s,rs))c=(LineupModelCatalog)CatalogSerializer.Deserialize(r);
                    if(c!=null){c.SanitizeInPlace();c.Deduplicate();return c;}
                }
            }
            return new LineupModelCatalog();
        }
        public void Save(string file){
            var clean=Clone();
            clean.SanitizeInPlace();
            clean.Deduplicate();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));
            string tmp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
            var ws=new XmlWriterSettings{Encoding=new UTF8Encoding(false),Indent=true,CheckCharacters=true};
            try{
                using(var s=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                using(var w=XmlWriter.Create(s,ws))CatalogSerializer.Serialize(w,clean);
                if(File.Exists(file)){
                    try{File.Replace(tmp,file,file+".bak");}
                    catch(IOException){File.Replace(tmp,file,null);}
                }else{
                    File.Move(tmp,file);
                }
            }finally{if(File.Exists(tmp)){try{File.Delete(tmp);}catch{}}}
        }
        static bool Same(string a,string b){return string.Equals((a??"").Trim(),(b??"").Trim(),StringComparison.OrdinalIgnoreCase);}
        static string Norm(string s){
            if(string.IsNullOrEmpty(s))return "";
            string t=Regex.Replace(s.Trim(),@"\(\s*0[_,]0\s*\)|_0$|-\s*副本.*$","",RegexOptions.IgnoreCase);
            return Regex.Replace(t,@"[\s_\-]+","").ToUpperInvariant();
        }
        // 仅在看起来是真实文件路径或以已知 CAD 扩展名结尾时剥离目录与扩展名；
        // 绝不对自由型号文本调用 Path.GetFileNameWithoutExtension，防止 "R-2000iC/210L" / "RH400.130.N30" 被截断，或含 '"' 时抛 ArgumentException。
        static string StripCadFilePathAndExt(string raw){
            if(string.IsNullOrWhiteSpace(raw))return "";
            string s=raw.Trim();
            int bs=s.LastIndexOf('\\');
            if(bs>=0&&bs+1<s.Length)s=s.Substring(bs+1);
            s=Regex.Replace(s,@"\.(par|psm|asm|pwd|dft|stp|step|igs|iges|x_t|x_b|prt|sldprt|sldasm)$","",RegexOptions.IgnoreCase);
            return s.Trim();
        }
        // 根据 CAD 零件文件名或属性名反查最匹配的型号库条目（忽略下划线/连字符/(0_0) 差异）
        public LineupModelEntry MatchByFileName(string fileNameOrTitle,string preferredCategory=null){
            string stem=Norm(StripCadFilePathAndExt(fileNameOrTitle));
            if(stem.Length<4)return null;
            var candidates=Entries.Where(e=>e.Category!="阀岛"&&!string.IsNullOrWhiteSpace(e.Model)).ToList();
            if(!string.IsNullOrEmpty(preferredCategory)){
                var exact=MatchInList(stem,candidates.Where(e=>Same(e.Category,preferredCategory)));
                if(exact!=null)return exact;
            }
            return MatchInList(stem,candidates);
        }
        static LineupModelEntry MatchInList(string normStem,IEnumerable<LineupModelEntry> list){
            LineupModelEntry best=null;int bestLen=0;
            foreach(var e in list){
                string nm=Norm(e.Model);if(nm.Length<4)continue;
                if(normStem==nm)return e;
                if(normStem.Contains(nm)&&nm.Length>bestLen){best=e;bestLen=nm.Length;}
                else if(nm.Contains(normStem)&&normStem.Length>=6&&normStem.Length>bestLen){best=e;bestLen=normStem.Length;}
            }
            return best;
        }
        public int Learn(IEnumerable<LineupRecord> records){
            int count=0;foreach(var r in records){
                string category=LineupTableData.Category(r.Category);
                if(string.IsNullOrWhiteSpace(r.Model)||string.IsNullOrWhiteSpace(r.Brand))continue;
                if((category=="气缸传感器"||category=="普通传感器")&&string.IsNullOrWhiteSpace(r.Connector))continue;
                if(Entries.Any(e=>Same(e.Category,category)&&Same(e.Model,r.Model)&&Same(e.Brand,r.Brand)&&Same(e.Connector,r.Connector)))continue;
                string model=r.Model.Trim(),brand=r.Brand.Trim();
                Entries.Add(new LineupModelEntry{Category=category,SubCategory=InferSubCategory(category,model,brand),Model=model,Brand=brand,Connector=(r.Connector??"").Trim()});count++;
            }return count;
        }
        public IEnumerable<LineupModelEntry> Match(string category,string model){return Entries.Where(e=>Same(e.Category,category)&&Same(e.Model,model));}
        public int Merge(LineupModelCatalog other){
            if(other==null||other.Entries==null)return 0;
            int added=0;
            foreach(var e in other.Entries){
                string category=LineupTableData.Category(e.Category);
                if(string.IsNullOrWhiteSpace(e.Model)||string.IsNullOrWhiteSpace(e.Brand))continue;
                if((category=="气缸传感器"||category=="普通传感器")&&string.IsNullOrWhiteSpace(e.Connector))continue;
                var hit=Entries.FirstOrDefault(x=>Same(x.Category,category)&&Same(x.Model,e.Model)&&Same(x.Brand,e.Brand)&&Same(x.Connector,e.Connector));
                if(hit!=null){
                    if(string.IsNullOrWhiteSpace(hit.SubCategory)&&!string.IsNullOrWhiteSpace(e.SubCategory))hit.SubCategory=e.SubCategory.Trim();
                    if(string.IsNullOrWhiteSpace(hit.Notes)&&!string.IsNullOrWhiteSpace(e.Notes))hit.Notes=e.Notes.Trim();
                    continue;
                }
                string model=e.Model.Trim(),brand=e.Brand.Trim();
                string sub=!string.IsNullOrWhiteSpace(e.SubCategory)?e.SubCategory.Trim():InferSubCategory(category,model,brand);
                Entries.Add(new LineupModelEntry{Category=category,SubCategory=sub,Model=model,Brand=brand,Connector=(e.Connector??"").Trim(),Notes=(e.Notes??"").Trim()});
                added++;
            }
            return added;
        }
        static string CompactHeaderFirstLine(string raw){
            if(string.IsNullOrWhiteSpace(raw))return "";
            string s=raw.Replace("\r\n","\n").Trim();
            int nl=s.IndexOf('\n');
            string first=(nl>=0?s.Substring(0,nl):s).Trim();
            return Regex.Replace(first,@"\s+","");
        }
        public static LineupModelCatalog Import(string text,char delimiter,string defaultCategory){
            var catalog=new LineupModelCatalog();var rows=LineupTableData.ReadDelimited(text,delimiter).Where(r=>r.Any(c=>!string.IsNullOrWhiteSpace(c))).ToList();if(rows.Count==0)return catalog;
            var header=rows[0];
            int model=-1,brand=-1,connector=-1,category=-1,subCategory=-1,notes=-1;
            bool hasBomNumber=false;
            for(int i=0;i<header.Length;i++){
                string h=CompactHeaderFirstLine(header[i]);
                if(h.Length==0)continue;
                if(h=="编号"||h.StartsWith("编号")||h.Equals("SK编号",StringComparison.OrdinalIgnoreCase)||h.StartsWith("SK编号",StringComparison.OrdinalIgnoreCase))hasBomNumber=true;
                else if(model<0&&(h=="型号"||h.StartsWith("型号")))model=i;
                else if(brand<0&&(h=="品牌"||h.StartsWith("品牌")))brand=i;
                else if(connector<0&&(h=="传感器接口类型"||h=="接口类型"||h.StartsWith("传感器接口")))connector=i;
                else if(category<0&&(h=="类别"||h=="大类"))category=i;
                else if(subCategory<0&&(h=="细分类型"||h=="子分类"||h=="类型"))subCategory=i;
                else if(notes<0&&(h=="备注"||h.StartsWith("备注")))notes=i;
            }
            // 从 BOM 清单提取型号允许草稿表中存在重复编号，与 Excel 提取通道保持一致
            if(hasBomNumber){catalog.Learn(LineupTableData.Parse(text,defaultCategory,delimiter,true).Records);return catalog;}
            if(model<0||brand<0)throw new InvalidDataException("型号库表头至少包含：型号、品牌；传感器还需要接口类型。可加类别列。");
            for(int i=1;i<rows.Count;i++){
                var row=rows[i];Func<int,string> at=index=>index>=0&&index<row.Length?row[index]:"";
                string kind=LineupTableData.Category(category<0?defaultCategory:at(category));
                if(!LineupTableData.Categories.Contains(kind)&&kind!="阀岛")throw new InvalidDataException("型号库第 "+(i+1)+" 行的类别无效。");
                string m=at(model).Trim(),b=at(brand).Trim(),c=at(connector).Trim(),sub=at(subCategory).Trim(),nt=at(notes).Trim();
                if(m.Length==0||b.Length==0||((kind=="气缸传感器"||kind=="普通传感器")&&c.Length==0))throw new InvalidDataException("型号库第 "+(i+1)+" 行字段不完整。");
                if(sub.Length==0)sub=InferSubCategory(kind,m,b);
                if(!catalog.Entries.Any(e=>Same(e.Category,kind)&&Same(e.Model,m)&&Same(e.Brand,b)&&Same(e.Connector,c)))
                    catalog.Entries.Add(new LineupModelEntry{Category=kind,SubCategory=sub,Model=m,Brand=b,Connector=c,Notes=nt});
            }
            return catalog;
        }
    }
}
