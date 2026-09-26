using System;

namespace TianGongCadSuite.Licensing {
    // 授权档位。档位编号是激活码载荷的一部分，一旦发布不得改变含义。
    public sealed class LicensePlan {
        public readonly byte Code;
        public readonly string Id;
        public readonly string Name;
        public readonly int Days;
        internal LicensePlan(byte code,string id,string name,int days){Code=code;Id=id;Name=name;Days=days;}
    }

    public static class LicensePlans {
        // 乱序编号：档位不是简单的 1/2/3，避免按序号猜测档位字段。
        public const byte Monthly = 11;
        public const byte HalfYear = 27;
        public const byte Yearly = 42;

        static readonly LicensePlan[] All = new LicensePlan[]{
            new LicensePlan(Monthly,"M","一个月",30),
            new LicensePlan(HalfYear,"H","半年",183),
            new LicensePlan(Yearly,"Y","一年",365)
        };

        public static LicensePlan[] Catalog { get { return (LicensePlan[])All.Clone(); } }

        public static LicensePlan Find(byte code){
            for(int i=0;i<All.Length;i++)if(All[i].Code==code)return All[i];
            return null;
        }

        public static LicensePlan FindById(string id){
            if(string.IsNullOrEmpty(id))return null;
            for(int i=0;i<All.Length;i++)if(string.Equals(All[i].Id,id,StringComparison.OrdinalIgnoreCase))return All[i];
            return null;
        }
    }
}
