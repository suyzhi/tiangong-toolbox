// 作废码清单（黑名单）。由管理员工具 `revoke` / `export` 生成后覆盖本文件，再重新编译插件。
// 语义：清单里出现的码ID，在任何机器上都不能激活；已经激活的机器在下次校验时也会失效。
// 注意：这是**离线吊销**，只对装了「带这份清单的插件版本」的客户端生效；
//      没有联网心跳，所以已发出的旧版本插件无法被即时吊销（见 LICENSE.md 第 5 节）。
// 码ID 是激活码载荷的 SHA-256 前 5 字节，8 个字符，形如 `abcd-efgh`；管理员工具的
// `new` / `list` 都会打印出来。本文件默认是空表，可以直接提交。
using System;

namespace TianGongCadSuite.Licensing {
    internal static class LicenseRevoked {
        // 生成时间戳（管理员工具写入，仅用于追溯，不参与校验）。
        internal static readonly string GeneratedStamp = "";

        // 作废码ID列表，只写 8 个字符的 ID，不写完整激活码。
        internal static readonly string[] GeneratedIds = new string[]{
        };

        // 判断某个码ID是否已作废。测试可以用 LicenseTestHooks.RevokedOverride 注入一份临时清单。
        internal static bool Contains(string codeId){
            if(string.IsNullOrEmpty(codeId))return false;
            string[] ids = LicenseTestHooks.RevokedOverride ?? GeneratedIds;
            if(ids == null || ids.Length == 0)return false;
            string needle = LicenseCodec.NormalizeId(codeId);
            if(needle.Length == 0)return false;
            for(int i = 0; i < ids.Length; i++){
                if(string.Equals(LicenseCodec.NormalizeId(ids[i]), needle, StringComparison.Ordinal))return true;
            }
            return false;
        }

        internal static int Count {
            get {
                string[] ids = LicenseTestHooks.RevokedOverride ?? GeneratedIds;
                return ids == null ? 0 : ids.Length;
            }
        }
    }
}
