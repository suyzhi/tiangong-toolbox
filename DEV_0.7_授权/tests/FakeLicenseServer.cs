using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using TianGongCadSuite.Licensing;

namespace PanelTests {
    // 进程内的"假授权服务器"：规则与 server/tg_license_server.py 的 Ledger.decide 一致，
    // 回执用临时生成的服务端密钥签名，插件经 LicenseTestHooks.OnlineTransport 调到这里。
    internal sealed class FakeLicenseServer : IDisposable {
        sealed class Entry {
            public string Machine;
            public int Used;
            public bool Revoked;
        }

        readonly ECDsaCng key = new ECDsaCng(256);
        readonly Dictionary<string,Entry> codes = new Dictionary<string,Entry>();

        public readonly byte[] PublicBlob;
        public int Quota = 2;
        public bool Offline;
        public int Calls;
        // 故意回一个对不上的 nonce，模拟重放旧回执。
        public bool ReplayNonce;

        public FakeLicenseServer(){
            PublicBlob = key.Key.Export(CngKeyBlobFormat.EccPublicBlob);
        }

        public void Install(){
            LicenseTestHooks.OnlineTransport = Handle;
            LicenseTestHooks.ServerKeyOverride = PublicBlob;
            LicenseTestHooks.OnlineSynchronous = true;
        }

        public void Revoke(string codeId,bool revoked){ Get(codeId).Revoked = revoked; }
        public string MachineOf(string codeId){ return Get(codeId).Machine; }

        Entry Get(string codeId){
            Entry entry;
            if(!codes.TryGetValue(codeId, out entry)){ entry = new Entry(); codes[codeId] = entry; }
            return entry;
        }

        public string Handle(string body){
            Calls++;
            if(Offline)return null;
            Dictionary<string,object> request = LicenseOnline.ParseJson(body);
            if(request == null)return "{\"error\":\"bad json\"}";
            string op = LicenseOnline.Text(request, "op");
            byte[] payload = Unhex(LicenseOnline.Text(request, "payload"));
            byte[] signature = Unhex(LicenseOnline.Text(request, "sig"));
            string machine = LicenseOnline.Text(request, "machine");
            string nonce = LicenseOnline.Text(request, "nonce");
            LicensePayload parsed = payload == null ? null : LicensePayload.FromBytes(payload, 0);
            if(parsed == null || parsed.Plan == null)return "{\"error\":\"bad payload\"}";
            if(!LicenseSignature.Verify(payload, signature))return "{\"error\":\"bad signature\"}";

            string codeId = LicenseCodec.IdOf(payload);
            Entry entry = Get(codeId);
            int today = LicenseLibrary.Today;
            string status = "ok", result = "";
            int left = Math.Max(Quota - entry.Used, 0);
            if(entry.Revoked)status = "revoked";
            else if(today >= parsed.DayOffset + parsed.Plan.Days)status = "expired";
            else if(entry.Machine == null){ entry.Machine = machine; result = "bound"; }
            else if(entry.Machine == machine)result = "already";
            else if(op == "check")status = "moved";
            else if(left <= 0){ status = "quota"; left = 0; }
            else{ entry.Machine = machine; entry.Used++; left--; result = "rebound"; }

            string receipt = "{\"v\":1,\"code\":\"" + codeId + "\",\"machine\":\"" + machine
                + "\",\"nonce\":\"" + (ReplayNonce ? "00000000000000000000000000000000" : nonce)
                + "\",\"op\":\"" + op + "\",\"status\":\"" + status + "\",\"result\":\"" + result
                + "\",\"left\":" + left + ",\"quota\":" + Quota + ",\"day\":" + today + ",\"time\":0}";
            byte[] raw = Encoding.ASCII.GetBytes(receipt);
            byte[] sig = key.SignData(raw, HashAlgorithmName.SHA256);
            return "{\"receipt\":\"" + Convert.ToBase64String(raw) + "\",\"sig\":\"" + Convert.ToBase64String(sig) + "\"}";
        }

        static byte[] Unhex(string text){
            if(string.IsNullOrEmpty(text) || text.Length % 2 != 0)return null;
            byte[] data = new byte[text.Length / 2];
            for(int i = 0; i < data.Length; i++)data[i] = Convert.ToByte(text.Substring(i * 2, 2), 16);
            return data;
        }

        public void Dispose(){ key.Dispose(); }
    }
}
