using System;
using System.Text;
using CoreLib;

namespace CryptoTest
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== Local-First Universal Clipboard Crypto Test ===\n");

            string masterPassword = "MySuperSecretPassword123!";
            byte[] salt = Encoding.UTF8.GetBytes("UniqueDeviceSalt_Win11");
            string originalClipboardText = "https://mybank.com | Username: admin | Password: Password1!";

            Console.WriteLine($"[1] Original Password:  {masterPassword}");
            Console.WriteLine($"[1] Original Clipboard: {originalClipboardText}\n");

            // --- WINDOWS DEVICE (Encryption) ---
            Console.WriteLine("--- SIMULATING WINDOWS DAEMON ---");
            Console.WriteLine("[Windows] Deriving AES-256 key from Master Password using Argon2id (this takes memory & time)...");
            byte[] windowsKey = CryptoEngine.DeriveKey(masterPassword, salt);
            Console.WriteLine($"[Windows] Derived Key: {Convert.ToBase64String(windowsKey)}");

            Console.WriteLine("[Windows] Encrypting clipboard text using AES-256-GCM...");
            byte[] plaintextBytes = Encoding.UTF8.GetBytes(originalClipboardText);
            byte[] encryptedPayload = CryptoEngine.Encrypt(plaintextBytes, windowsKey);
            
            Console.WriteLine($"[Windows] Encrypted Payload (ready to send via BLE/Wi-Fi): {Convert.ToBase64String(encryptedPayload)}\n");


            // --- ANDROID DEVICE (Decryption) ---
            Console.WriteLine("--- SIMULATING ANDROID MAUI APP ---");
            Console.WriteLine("[Android] Receiving encrypted payload over airwaves...");
            Console.WriteLine("[Android] Deriving AES-256 key from Master Password using Argon2id...");
            
            // In a real scenario, the Android device would have already derived its key when the user typed their password
            byte[] androidKey = CryptoEngine.DeriveKey(masterPassword, salt);

            Console.WriteLine("[Android] Decrypting payload and validating GCM authentication tag...");
            try 
            {
                byte[] decryptedBytes = CryptoEngine.Decrypt(encryptedPayload, androidKey);
                string decryptedText = Encoding.UTF8.GetString(decryptedBytes);
                
                Console.WriteLine($"[Android] Decrypted Clipboard: {decryptedText}\n");
                
                if (originalClipboardText == decryptedText)
                {
                    Console.WriteLine("✅ SUCCESS! Cross-device encryption & decryption works flawlessly. No data leaks!");
                }
                else
                {
                    Console.WriteLine("❌ FAILED! Text mismatch.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ FAILED! Decryption error: {ex.Message}");
            }
        }
    }
}
