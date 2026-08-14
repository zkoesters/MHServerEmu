using System.Security.Cryptography;
using System.Text;
using MHServerEmu.Core.Helpers;

namespace MHServerEmu.Core.Tests.Helpers
{
    public class CryptographyHelperTests
    {
        [Fact]
        public void VerifyPassword_Pbkdf2Sha512CompatibilityVector_ReturnsExpectedResult()
        {
            const string password = "existing-password";
            byte[] salt = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 210000, HashAlgorithmName.SHA512, 64);

            Assert.True(CryptographyHelper.VerifyPassword(password, hash, salt));
            Assert.False(CryptographyHelper.VerifyPassword("wrong-password", hash, salt));
        }
    }
}
