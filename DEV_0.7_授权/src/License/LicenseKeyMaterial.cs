using System;
using System.Security.Cryptography;
using System.Text;

namespace TianGongCadSuite.Licensing {
    // 内嵌验签公钥：只有公钥随插件分发，签发私钥只存在于管理员机器。
    // 公钥不是秘密，但不能让"改两行代码换成自己的公钥"成立：
    // 坐标做了两层掩码（源码不改算不出来），并对掩码常量本身做语义指纹，
    // 任何一处常量被改写，验签与自检都会同时失败，整条授权链失效。
    internal static class LicenseKeyMaterial {
        internal const string DomainA = "TGS-KX-01";
        internal const string DomainB = "TGS-KX-02";

        internal static byte[] CoordinateX(){ return Unmask(LicenseKeySlot.MaskX, LicenseKeySlot.ValueX, LicenseKeySlot.SeedX); }
        internal static byte[] CoordinateY(){ return Unmask(LicenseKeySlot.MaskY, LicenseKeySlot.ValueY, LicenseKeySlot.SeedY); }

        internal static byte[] Unmask(string mask,string encoded,string seed){
            if(string.IsNullOrEmpty(mask) || string.IsNullOrEmpty(encoded) || string.IsNullOrEmpty(seed))throw new InvalidOperationException("key material");
            if(mask.Length != 64)throw new InvalidOperationException("key mask");
            byte[] value;
            try{ value = Convert.FromBase64String(encoded); }
            catch(FormatException){ throw new InvalidOperationException("key value"); }
            if(value.Length != 32)throw new InvalidOperationException("key value");
            byte[] stream = Stream(seed, 32);
            byte[] result = new byte[32];
            for(int i = 0; i < 32; i++)result[i] = (byte)(value[i] ^ stream[i] ^ Nibble(mask, i));
            return result;
        }

        internal static byte[] Stream(string domain,int length){
            byte[] stream = new byte[length];
            byte[] prefix = Encoding.ASCII.GetBytes(domain);
            int written = 0;
            uint counter = 0;
            while(written < length){
                byte[] block = new byte[prefix.Length + 4];
                Buffer.BlockCopy(prefix, 0, block, 0, prefix.Length);
                block[prefix.Length] = (byte)(counter >> 24);
                block[prefix.Length + 1] = (byte)(counter >> 16);
                block[prefix.Length + 2] = (byte)(counter >> 8);
                block[prefix.Length + 3] = (byte)counter;
                using(SHA256 hash = SHA256.Create()){
                    byte[] digest = hash.ComputeHash(block);
                    int take = Math.Min(digest.Length, length - written);
                    Buffer.BlockCopy(digest, 0, stream, written, take);
                    written += take;
                }
                counter++;
            }
            return stream;
        }

        static byte Nibble(string mask,int index){
            int high = Digit(mask[index * 2]);
            int low = Digit(mask[index * 2 + 1]);
            return (byte)((high << 4) | low);
        }

        static int Digit(char c){
            if(c >= '0' && c <= '9')return c - '0';
            if(c >= 'a' && c <= 'f')return c - 'a' + 10;
            if(c >= 'A' && c <= 'F')return c - 'A' + 10;
            throw new InvalidOperationException("key mask");
        }

        // 组装 CNG ECC 公钥 BLOB：BCRYPT_ECDSA_PUBLIC_P256_MAGIC + cbKey=32 + X + Y
        internal static byte[] BuildBlob(){
            byte[] x = CoordinateX();
            byte[] y = CoordinateY();
            if(x.Length != 32 || y.Length != 32)throw new InvalidOperationException("key size");
            byte[] blob = new byte[72];
            blob[0] = 0x45; blob[1] = 0x43; blob[2] = 0x53; blob[3] = 0x31;
            blob[4] = 32; blob[5] = 0; blob[6] = 0; blob[7] = 0;
            Buffer.BlockCopy(x, 0, blob, 8, 32);
            Buffer.BlockCopy(y, 0, blob, 40, 32);
            return blob;
        }

        // 公钥与掩码常量的语义指纹：用于"代码被改过"自检，换密钥后需重新构建。
        internal static uint SemanticPrint(){
            unchecked{
                uint acc = 0x811C9DC5;
                byte[] blob = BuildBlob();
                for(int i = 0; i < blob.Length; i++){ acc ^= blob[i]; acc *= 16777619; }
                string all = LicenseKeySlot.MaskX + LicenseKeySlot.MaskY + LicenseKeySlot.SeedX + LicenseKeySlot.SeedY;
                for(int i = 0; i < all.Length; i++){ acc ^= (byte)all[i]; acc *= 16777619; }
                return acc;
            }
        }
    }
}
