using System.Security.Cryptography;

namespace MHServerEmu.Core.Helpers
{
    public readonly record struct PasswordHashMetadata(string Algorithm, int FormatVersion, int Iterations, int KeySize);

    public static class CryptographyHelper
    {
        public static PasswordHashMetadata DefaultPasswordHashMetadata { get; } = new("PBKDF2-HMAC-SHA512", 1, 210000, 64);

        public static byte[] HashPassword(string password, out byte[] salt)
        {
            salt = RandomNumberGenerator.GetBytes(DefaultPasswordHashMetadata.KeySize);
            return Rfc2898DeriveBytes.Pbkdf2(password, salt, DefaultPasswordHashMetadata.Iterations, HashAlgorithmName.SHA512, DefaultPasswordHashMetadata.KeySize);
        }

        public static bool VerifyPassword(string password, byte[] hash, byte[] salt)
        {
            byte[] hashToCompare = Rfc2898DeriveBytes.Pbkdf2(password, salt, DefaultPasswordHashMetadata.Iterations, HashAlgorithmName.SHA512, DefaultPasswordHashMetadata.KeySize);
            return CryptographicOperations.FixedTimeEquals(hashToCompare, hash);
        }

        public static byte[] GenerateToken(int size = 32)
        {
            // The game uses AES-256 CBC encryption for tokens.
            // AuthServer sends a token and a session key, and when the client connects to a FES it adds IV, encrypts the token and sends it.
            // The encrypted token in the dump we have is 48 bytes. We can get a similar token by encrypting 32 bytes of random data.
            return RandomNumberGenerator.GetBytes(size);
        }

        public static byte[] GenerateAesKey(int size = 256)
        {
            using (Aes aesAlgorithm = Aes.Create())
            {
                aesAlgorithm.KeySize = size;
                aesAlgorithm.GenerateKey();
                return aesAlgorithm.Key;
            }
        }

        public static byte[] EncryptToken(byte[] tokenToEncrypt, byte[] key, out byte[] iv)
        {
            using (Aes aesAlgorithm = Aes.Create())
            {
                aesAlgorithm.Key = key;
                aesAlgorithm.GenerateIV();
                iv = aesAlgorithm.IV;
                ICryptoTransform encryptor = aesAlgorithm.CreateEncryptor();

                using (MemoryStream memoryStream = new())
                using (CryptoStream cryptoStream = new(memoryStream, encryptor, CryptoStreamMode.Write))
                {
                    cryptoStream.Write(tokenToEncrypt, 0, tokenToEncrypt.Length);
                    return memoryStream.ToArray();
                }
            }
        }

        public static bool TryDecryptToken(byte[] encryptedToken, byte[] key, byte[] iv, out byte[] decryptedToken)
        {
            using (Aes aesAlgorithm = Aes.Create())
            {
                aesAlgorithm.Key = key;
                aesAlgorithm.IV = iv;
                ICryptoTransform decryptor = aesAlgorithm.CreateDecryptor();

                try
                {
                    using (MemoryStream memoryStream = new(encryptedToken))
                    using (CryptoStream cryptoStream = new(memoryStream, decryptor, CryptoStreamMode.Read))
                    using (MemoryStream decryptionBuffer = new())
                    {
                        cryptoStream.CopyTo(decryptionBuffer);
                        decryptedToken = decryptionBuffer.ToArray();
                        return true;
                    }
                }
                catch
                {
                    decryptedToken = null;
                    return false;
                }
            }
        }

        public static bool VerifyToken(byte[] credentialsToken, byte[] sessionToken)
        {
            return CryptographicOperations.FixedTimeEquals(credentialsToken, sessionToken);
        }
    }
}
