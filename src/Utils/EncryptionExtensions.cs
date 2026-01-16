// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Utils;

public static class EncryptionExtensions
{
    const int KeySize = 32; // 256 bits
    const int TagSize = 16; // 128-bit authentication tag
    const int NonceSize = 12; // 96-bit nonce

    public static string Encrypt(string plainText, byte[] encryptionKey)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plainText);

        var encryptedBytes = EncryptionExtensions.Encrypt(plainBytes, encryptionKey);

        var encryptedText = Convert.ToBase64String(encryptedBytes);

        return encryptedText;
    }

    public static string Decrypt(string encryptedText, byte[] encryptionKey)
    {
        var decryptedBytes = Convert.FromBase64String(encryptedText);

        decryptedBytes = EncryptionExtensions.Decrypt(decryptedBytes, encryptionKey);

        var plainText = Encoding.UTF8.GetString(decryptedBytes);

        return plainText;
    }

    public static byte[] Encrypt(byte[] plainBytes, byte[] encryptionKey)
    {
        // Generate random nonce
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var tag = new byte[TagSize];
        var cipherText = new byte[plainBytes.Length];

        using (var aes = new AesGcm(encryptionKey, TagSize))
        {
            aes.Encrypt(nonce, plainBytes, cipherText, tag);
        }

        // Combination: nonce + tag + cipherText
        var result = new byte[nonce.Length + tag.Length + cipherText.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipherText, 0, result, nonce.Length + tag.Length, cipherText.Length);

        return result;
    }

    public static byte[] Decrypt(byte[] encryptedData, byte[] encryptionKey)
    {
        if (encryptedData.Length < NonceSize + TagSize)
        {
            throw new ArgumentException("Invalid");
        }

        // Splite
        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var cipherText = new byte[encryptedData.Length - NonceSize - TagSize];

        Buffer.BlockCopy(encryptedData, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(encryptedData, NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(encryptedData, NonceSize + TagSize, cipherText, 0, cipherText.Length);

        var plainBytes = new byte[cipherText.Length];

        using (var aes = new AesGcm(encryptionKey, TagSize))
        {
            aes.Decrypt(nonce, cipherText, tag, plainBytes);
        }

        return plainBytes;
    }

    public static byte[] DeriveKeyFromMaster(string masterKey, string saltKey)
    {
        byte[] salt = Encoding.UTF8.GetBytes(saltKey);

        return Rfc2898DeriveBytes.Pbkdf2(
            password: masterKey,
            salt: salt,
            iterations: 100000,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: KeySize);
    }

    public static byte[] GenerateRandomKey()
    {
        var key = new byte[KeySize];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    public static (byte[] Key, byte[] Salt) DeriveKeyWithRandomSalt(string masterKey, int saltSize = 16)
    {
        byte[] salt = new byte[saltSize];
        RandomNumberGenerator.Fill(salt);

        byte[] key = Rfc2898DeriveBytes.Pbkdf2(
            password: masterKey,
            salt: salt,
            iterations: 100000,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: KeySize);

        return (key, salt);
    }
}
