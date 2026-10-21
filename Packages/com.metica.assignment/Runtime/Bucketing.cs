using System.Security.Cryptography;
using System.Text;

namespace Metica.Assignment {
    /// <summary>
    /// bucket = uint64_be(SHA256(UTF8(experiment_id + ":" + user_id))[0..8]) % 10000
    /// group  = bucket &lt; metica_percentage_bp ? Metica : Baseline
    /// </summary>
    internal static class Bucketing {
        public const int BucketCount = 10000;

        public static int Bucket(string experimentId, string userId) {
            byte[] input = Encoding.UTF8.GetBytes(experimentId + ":" + userId);
            byte[] hash;
            using (SHA256 sha = SHA256.Create()) {
                hash = sha.ComputeHash(input);
            }
            ulong value = 0;
            for (int i = 0; i < 8; i++) {
                value = (value << 8) | hash[i];
            }
            return (int)(value % BucketCount);
        }

        public static Group GroupFor(int bucket, int meticaPercentageBasisPoints) {
            if (bucket < meticaPercentageBasisPoints) {
                return Group.Metica;
            }
            return Group.Baseline;
        }
    }
}
