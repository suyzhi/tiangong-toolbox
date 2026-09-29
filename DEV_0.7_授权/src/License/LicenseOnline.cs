using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace TianGongCadSuite.Licensing {
    // 联网授权回执：服务端用自己的私钥签名，插件只认内嵌服务端公钥（LicenseServerSlot）签过的回执。
    // 回执原文（base64）与签名一起存进激活文件，每次校验都重新验签，改激活文件伪造"在线状态"没有用。
    internal sealed class LicenseReceipt {
        public string Raw;
        public string Signature;
        public string CodeId;
        public string Machine;
        public string Nonce;
        public string Op;
        public string Status;
        public string Result;
        public int Day;
        public int Left;
        public int Quota;

        public bool Ok { get { return Status == "ok"; } }

        internal static LicenseReceipt Open(string raw,string signature){
            if(string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(signature))return null;
            byte[] data;
            byte[] sig;
            try{ data = Convert.FromBase64String(raw); sig = Convert.FromBase64String(signature); }
            catch(FormatException){ return null; }
            if(data.Length > 4096 || !LicenseOnline.VerifyServer(data, sig))return null;
            Dictionary<string,object> map = LicenseOnline.ParseJson(Encoding.UTF8.GetString(data));
            if(map == null || LicenseOnline.Number(map, "v") != 1)return null;
            LicenseReceipt receipt = new LicenseReceipt();
            receipt.Raw = raw;
            receipt.Signature = signature;
            receipt.CodeId = LicenseOnline.Text(map, "code");
            receipt.Machine = LicenseOnline.Text(map, "machine");
            receipt.Nonce = LicenseOnline.Text(map, "nonce");
            receipt.Op = LicenseOnline.Text(map, "op");
            receipt.Status = LicenseOnline.Text(map, "status");
            receipt.Result = LicenseOnline.Text(map, "result");
            receipt.Day = (int)LicenseOnline.Number(map, "day");
            receipt.Left = (int)LicenseOnline.Number(map, "left");
            receipt.Quota = (int)LicenseOnline.Number(map, "quota");
            if(receipt.CodeId == null || receipt.Machine == null || receipt.Status == null || receipt.Day <= 0)return null;
            return receipt;
        }

        // 激活被服务器拒绝时给用户看的话。
        internal string RejectMessage(LicenseCode code){
            switch(Status){
                case "revoked": return "该激活码已被管理员停用（作废），请联系管理员重新签发。";
                case "expired": return "该激活码已于 " + LicenseTime.Format(code.ExpiryDate) + " 到期。";
                case "quota": return "该激活码已在另一台电脑上激活，自助换机次数已用完（共 " + Quota + " 次）。请联系管理员处理。";
                case "moved": return "该激活码已在另一台电脑上激活。";
            }
            return "授权服务器拒绝了这次激活（" + Status + "）。";
        }
    }

    // 联网授权（DEV 0.8）：
    //   激活：必须联网，服务器登记"码 → 本机"；码已在别的电脑上时，按剩余自助换机次数决定能否转过来。
    //   复核：回执超过 1 天就在后台静默复核；超过 7 天（宽限期）还没复核成功，当场联网一次，失败就停用。
    //   作废 / 转出：服务器回执里的结论落盘，本机下次校验立即生效，不用等插件发新版本。
    internal static class LicenseOnline {
        internal const int GraceDays = 7;
        internal const int RefreshDays = 1;
        const int TimeoutMs = 8000;
        const int RetrySeconds = 30;
        const int BackgroundMinutes = 30;

        static readonly object sync = new object();
        static DateTime lastFailure = DateTime.MinValue;
        static DateTime lastBackground = DateTime.MinValue;
        static int backgroundBusy;

        // 最近一次联网失败的原因，显示在状态说明里。
        internal static string LastError;

        static bool Synchronous {
            get {
#if TG_DEV_BUILD
                return LicenseTestHooks.OnlineSynchronous;
#else
                return false;
#endif
            }
        }

        internal static void ResetThrottle(){
            lock(sync){ lastFailure = DateTime.MinValue; lastBackground = DateTime.MinValue; }
        }

        // ---------- 校验（LicenseLibrary.Current 调用；返回 Valid 表示联网这一关通过） ----------

        internal static LicenseStatus Evaluate(ActivationRecord record,LicenseCode code,out string detail){
            detail = null;
            int today = LicenseLibrary.Today;
            LicenseReceipt receipt = Stored(record, code);
            if(receipt == null || today - receipt.Day > GraceDays){
                LicenseReceipt fresh = Refresh(code, false);
                if(fresh == null){
                    detail = LastError;
                    return LicenseStatus.OnlineRequired;
                }
                receipt = fresh;
            }else if(today - receipt.Day >= RefreshDays){
                if(Synchronous){
                    LicenseReceipt fresh = Refresh(code, true);
                    if(fresh != null)receipt = fresh;
                }else RefreshInBackground(code);
            }
            // 本机时间比服务器上次给的日期还早一周以上：时间被回调过。
            if(today + LicenseLibrary.DayTolerance < receipt.Day)return LicenseStatus.ClockTampered;
            switch(receipt.Status){
                case "ok": return LicenseStatus.Valid;
                case "revoked": return LicenseStatus.Revoked;
                case "expired": return LicenseStatus.Expired;
                case "moved":
                case "quota": return LicenseStatus.MovedAway;
            }
            detail = "授权服务器返回了无法识别的结论：" + receipt.Status;
            return LicenseStatus.OnlineRequired;
        }

        // 激活文件里存的回执：验签通过、且确实是"这个码 + 本机"的才算数。
        internal static LicenseReceipt Stored(ActivationRecord record,LicenseCode code){
            if(record == null || code == null)return null;
            LicenseReceipt receipt = LicenseReceipt.Open(record.Receipt, record.ReceiptSignature);
            if(receipt == null)return null;
            if(receipt.CodeId != code.CodeId || receipt.Machine != Hex(LicenseLibrary.MachineShort))return null;
            return receipt;
        }

        // 联网复核一次并把结论落盘。force=true 时忽略失败冷却（用户点了"重新联网验证"）。
        internal static LicenseReceipt Refresh(LicenseCode code,bool force){
            if(!force && !Synchronous){
                lock(sync){ if((DateTime.UtcNow - lastFailure).TotalSeconds < RetrySeconds)return null; }
            }
            string error;
            LicenseReceipt receipt = Call("check", code, LicenseLibrary.MachineShort, out error);
            if(receipt == null){
                LastError = error;
                lock(sync){ lastFailure = DateTime.UtcNow; }
                return null;
            }
            LastError = null;
            Persist(code, receipt);
            return receipt;
        }

        static void RefreshInBackground(LicenseCode code){
            lock(sync){
                if((DateTime.UtcNow - lastBackground).TotalMinutes < BackgroundMinutes)return;
                lastBackground = DateTime.UtcNow;
            }
            if(Interlocked.CompareExchange(ref backgroundBusy, 1, 0) != 0)return;
            Thread worker = new Thread(delegate(){
                try{ Refresh(code, true); }
                catch(Exception){ }
                finally{ Interlocked.Exchange(ref backgroundBusy, 0); }
            });
            worker.IsBackground = true;
            worker.Name = "TG-License-Refresh";
            worker.Start();
        }

        // 回执写回激活文件。期间激活文件可能已经换成别的码（用户刚输了新码），那就丢弃这张回执。
        static void Persist(LicenseCode code,LicenseReceipt receipt){
            lock(sync){
                ActivationRecord record = LicenseStore.Load();
                if(record == null)return;
                LicenseCode current = LicenseCodec.Parse(record.Code);
                if(current == null || current.CodeId != code.CodeId)return;
                record.Receipt = receipt.Raw;
                record.ReceiptSignature = receipt.Signature;
                LicenseStore.Save(record);
            }
        }

        // 激活窗口的"重新联网验证"按钮。
        internal static LicenseReport RetryNow(){
            ActivationRecord record = LicenseStore.Load();
            LicenseCode code = record == null ? null : LicenseCodec.Parse(record.Code);
            if(code != null)Refresh(code, true);
            LicenseLibrary.ResetCache();
            return LicenseLibrary.Current();
        }

        // ---------- 协议 ----------

        // 发一次请求，拿回验签通过、且与本次请求逐项对得上的回执；失败返回 null 并给出原因。
        internal static LicenseReceipt Call(string op,LicenseCode code,byte[] machine,out string error){
            error = null;
            string nonce = Hex(Random(16));
            string machineHex = Hex(machine);
            StringBuilder body = new StringBuilder(320);
            body.Append("{\"v\":1,\"op\":\"").Append(op)
                .Append("\",\"payload\":\"").Append(Hex(code.Payload.ToBytes()))
                .Append("\",\"sig\":\"").Append(Hex(code.Signature))
                .Append("\",\"machine\":\"").Append(machineHex)
                .Append("\",\"nonce\":\"").Append(nonce)
                .Append("\",\"client\":\"").Append(LicenseConstants.Major).Append('.').Append(LicenseConstants.Minor)
                .Append("\"}");
            string response = Send(body.ToString(), out error);
            if(response == null)return null;
            Dictionary<string,object> map = ParseJson(response);
            if(map == null){ error = "授权服务器的应答无法识别。"; return null; }
            string refusal = Text(map, "error");
            if(refusal != null){ error = "授权服务器拒绝了请求：" + refusal; return null; }
            LicenseReceipt receipt = LicenseReceipt.Open(Text(map, "receipt"), Text(map, "sig"));
            if(receipt == null){ error = "授权服务器的回执验签失败（服务器地址被劫持，或插件与服务器的密钥不配套）。"; return null; }
            if(receipt.CodeId != code.CodeId || receipt.Machine != machineHex || receipt.Nonce != nonce || receipt.Op != op){
                error = "授权服务器的回执与本次请求不符。";
                return null;
            }
            return receipt;
        }

        static string Send(string body,out string error){
            error = null;
#if TG_DEV_BUILD
            if(LicenseTestHooks.OnlineTransport != null){
                string reply = LicenseTestHooks.OnlineTransport(body);
                if(reply == null)error = "无法连接授权服务器（测试：离线）。";
                return reply;
            }
#endif
            string url = LicenseServerSlot.Url;
            if(string.IsNullOrEmpty(url)){ error = "插件没有配置授权服务器地址（LicenseServerSlot 仍是占位），请联系管理员。"; return null; }
            try{
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json; charset=utf-8";
                request.Timeout = TimeoutMs;
                request.ReadWriteTimeout = TimeoutMs;
                request.UserAgent = "TianGongCadSuite/" + LicenseConstants.Major + "." + LicenseConstants.Minor;
                byte[] data = Encoding.UTF8.GetBytes(body);
                request.ContentLength = data.Length;
                using(Stream stream = request.GetRequestStream())stream.Write(data, 0, data.Length);
                using(HttpWebResponse response = (HttpWebResponse)request.GetResponse())return ReadBody(response);
            }catch(WebException e){
                HttpWebResponse response = e.Response as HttpWebResponse;
                if(response != null){
                    using(response){
                        string text = ReadBody(response);
                        // 服务器的拒绝理由（400/403/429）是 JSON，交给上层显示；其余按连接失败处理。
                        if(text != null && text.TrimStart().StartsWith("{"))return text;
                        error = "授权服务器返回 HTTP " + (int)response.StatusCode + "。";
                        return null;
                    }
                }
                error = "无法连接授权服务器（" + e.Status + "），请检查网络。";
                return null;
            }catch(Exception e){
                error = "联网验证失败：" + e.Message;
                return null;
            }
        }

        static string ReadBody(HttpWebResponse response){
            try{
                using(Stream stream = response.GetResponseStream())
                using(StreamReader reader = new StreamReader(stream, Encoding.UTF8)){
                    char[] buffer = new char[65536];
                    int read = reader.ReadBlock(buffer, 0, buffer.Length);
                    return new string(buffer, 0, read);
                }
            }catch(Exception){ return null; }
        }

        // ---------- 服务端公钥与小工具 ----------

        static byte[] ServerKeyBlob(){
#if TG_DEV_BUILD
            if(LicenseTestHooks.ServerKeyOverride != null)return LicenseTestHooks.ServerKeyOverride;
#endif
            byte[] x = Unhex(LicenseServerSlot.KeyX);
            byte[] y = Unhex(LicenseServerSlot.KeyY);
            if(x == null || y == null || x.Length != 32 || y.Length != 32)return null;
            byte[] blob = new byte[72];
            blob[0] = 0x45; blob[1] = 0x43; blob[2] = 0x53; blob[3] = 0x31;
            blob[4] = 32;
            Buffer.BlockCopy(x, 0, blob, 8, 32);
            Buffer.BlockCopy(y, 0, blob, 40, 32);
            return blob;
        }

        internal static bool VerifyServer(byte[] data,byte[] signature){
            byte[] blob = ServerKeyBlob();
            if(blob == null || data == null || signature == null || signature.Length != LicenseSignature.Size)return false;
            try{
                using(CngKey key = CngKey.Import(blob, CngKeyBlobFormat.EccPublicBlob))
                using(ECDsaCng ecdsa = new ECDsaCng(key)){
                    return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
                }
            }catch(CryptographicException){ return false; }
            catch(ArgumentException){ return false; }
        }

        internal static Dictionary<string,object> ParseJson(string text){
            if(string.IsNullOrEmpty(text))return null;
            try{ return new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string,object>; }
            catch(Exception){ return null; }
        }

        internal static string Text(Dictionary<string,object> map,string name){
            object value;
            if(!map.TryGetValue(name, out value) || value == null)return null;
            return value as string;
        }

        internal static long Number(Dictionary<string,object> map,string name){
            object value;
            if(!map.TryGetValue(name, out value) || value == null || value is string)return 0;
            try{ return Convert.ToInt64(value); }catch(Exception){ return 0; }
        }

        static byte[] Random(int size){
            byte[] data = new byte[size];
            using(RandomNumberGenerator rng = RandomNumberGenerator.Create())rng.GetBytes(data);
            return data;
        }

        internal static string Hex(byte[] data){
            if(data == null)return "";
            StringBuilder text = new StringBuilder(data.Length * 2);
            for(int i = 0; i < data.Length; i++)text.Append(data[i].ToString("x2"));
            return text.ToString();
        }

        static byte[] Unhex(string text){
            if(string.IsNullOrEmpty(text) || text.Length % 2 != 0)return null;
            byte[] data = new byte[text.Length / 2];
            for(int i = 0; i < data.Length; i++){
                int value;
                if(!int.TryParse(text.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out value))return null;
                data[i] = (byte)value;
            }
            return data;
        }
    }
}
