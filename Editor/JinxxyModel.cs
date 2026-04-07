#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace DenisHik.JinxxyEditor
{
    [Serializable]
    public class CurrentUser
    {
        public string Id;
        public string Name;
        public string username;
        public string Email;
        public string AvatarUrl;

        public static CurrentUser FromDto(UserDto dto)
        {
            if (dto == null)
                return null;

            return new CurrentUser
            {
                Id = dto.id,
                Name = dto.name,
                username = dto.username,
                Email = dto.email,
                AvatarUrl = dto.profile_image != null ? dto.profile_image.url : null
            };
        }
    }

    [Serializable]
    public class InventoryObject
    {
        public string Id;
        public string Name;
        public string Author;
        public string ImageUrl;
        public string ImageContentType;
        public string ImageExtension;
        public string VersionName;

        public bool IsGifImage => IsGifMedia(ImageContentType, ImageExtension);

        public static InventoryObject FromPayload(InventoryPayloadDto payload)
        {
            return new InventoryObject
            {
                Id = payload.id,
                Name = payload.item != null ? payload.item.name : null,
                Author = payload.item != null && payload.item.user != null ? payload.item.user.username : null,
                ImageUrl = payload.item != null && payload.item.cover != null ? payload.item.cover.url : null,
                ImageContentType = payload.item != null && payload.item.cover != null ? payload.item.cover.content_type : null,
                ImageExtension = payload.item != null && payload.item.cover != null ? payload.item.cover.extension : null,
                VersionName = payload.item != null && payload.item.version != null ? payload.item.version.name : null
            };
        }

        private static bool IsGifMedia(string contentType, string extension)
        {
            return string.Equals(extension, "gif", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase) ||
                   (!string.IsNullOrWhiteSpace(contentType) && contentType.StartsWith("image/gif", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Serializable]
    public class ProductDownloadFile
    {
        public string Id;
        public string FileName;
        public string Extension;
    }

    [Serializable]
    public class SavedJinxxyAccount
    {
        private const string SavedAccountFilePath = "UserSettings/JinxxySavedAccount.json";

        public string iconUrl;
        public string name;
        public string login;
        public string password;

        public bool HasLogin()
        {
            return !string.IsNullOrWhiteSpace(login) && !string.IsNullOrWhiteSpace(password);
        }

        public bool TryGetPassword(out string password)
        {
            password = null;

            if (string.IsNullOrWhiteSpace(this.password))
                return false;

            try
            {
                string decryptedPassword = DecryptString(this.password);
                if (string.IsNullOrEmpty(decryptedPassword))
                    return false;

                password = decryptedPassword;
                return true;
            }
            catch
            {
                password = null;
                return false;
            }
        }

        public void Save()
        {
            string path = GetSavedAccountPath();
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonUtility.ToJson(this, true), Encoding.UTF8);
        }

        public static SavedJinxxyAccount Create(string iconUrl, string name, string login, string password)
        {
            return new SavedJinxxyAccount
            {
                iconUrl = iconUrl,
                name = name,
                login = login,
                password = EncryptString(password)
            };
        }

        public static SavedJinxxyAccount Load()
        {
            string path = GetSavedAccountPath();
            if (!File.Exists(path))
                return null;

            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                return JsonUtility.FromJson<SavedJinxxyAccount>(json);
            }
            catch
            {
                return null;
            }
        }

        private static string GetSavedAccountPath()
        {
            return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), SavedAccountFilePath));
        }

        private static string EncryptString(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            byte[] bytes = Encoding.UTF8.GetBytes(value);
            ApplyPasswordMask(bytes);
            return Convert.ToBase64String(bytes);
        }

        private static string DecryptString(string encryptedValue)
        {
            if (string.IsNullOrWhiteSpace(encryptedValue))
                return null;

            byte[] bytes = Convert.FromBase64String(encryptedValue);
            ApplyPasswordMask(bytes);
            return Encoding.UTF8.GetString(bytes);
        }

        private static void ApplyPasswordMask(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return;

            byte[] key = Encoding.UTF8.GetBytes("JinxxySavedAccountPassword");
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] ^= key[i % key.Length];
        }
    }

    [Serializable]
    public class JinxxySessionStore
    {
        private const string SessionPrefKey = "Unity.Jinxxy.Session";

        public string UserId;
        public string Email;
        public List<CookiePair> Cookies = new List<CookiePair>();

        public bool HasAnyCookie()
        {
            return Cookies != null && Cookies.Count > 0;
        }

        public void SetCookie(string name, string value)
        {
            if (Cookies == null)
                Cookies = new List<CookiePair>();

            for (int i = 0; i < Cookies.Count; i++)
            {
                if (string.Equals(Cookies[i].Name, name, StringComparison.Ordinal))
                {
                    Cookies[i].Value = value;
                    return;
                }
            }

            Cookies.Add(new CookiePair { Name = name, Value = value });
        }

        public void Clear()
        {
            UserId = null;
            Email = null;
            Cookies?.Clear();
        }

        public void Save()
        {
            string json = JsonUtility.ToJson(this);
            EditorPrefs.SetString(SessionPrefKey, json);
        }

        public static JinxxySessionStore Load()
        {
            string json = EditorPrefs.GetString(SessionPrefKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
                return new JinxxySessionStore();

            try
            {
                return JsonUtility.FromJson<JinxxySessionStore>(json) ?? new JinxxySessionStore();
            }
            catch
            {
                return new JinxxySessionStore();
            }
        }
    }

    [Serializable]
    public class CookiePair
    {
        public string Name;
        public string Value;
    }

    [Serializable]
    public class LoginResponse
    {
        public LoginResponseData data;
        public GraphQlError[] errors;
    }

    [Serializable]
    public class LoginResponseData
    {
        public LoginEmailResult loginEmail;
        public UserDto user;
    }

    [Serializable]
    public class LoginEmailResult
    {
        public UserDto user;
    }

    [Serializable]
    public class GetUserCardResponse
    {
        public GetUserCardData data;
        public GraphQlError[] errors;
    }

    [Serializable]
    public class GetUserCardData
    {
        public UserDto user;
    }

    [Serializable]
    public class InventoryResponse
    {
        public InventoryResponseData data;
        public GraphQlError[] errors;
    }

    [Serializable]
    public class InventoryResponseData
    {
        public InventoryItemsDto inventory_items;
    }

    [Serializable]
    public class DownloadFileResponse
    {
        public DownloadFileResponseData data;
        public GraphQlError[] errors;
    }

    [Serializable]
    public class DownloadFileResponseData
    {
        public DownloadInventoryItemDto inventory_item;
    }

    [Serializable]
    public class DownloadInventoryItemDto
    {
        public string downloadFile;
    }

    [Serializable]
    public class InventoryItemsDto
    {
        public InventoryPayloadDto[] payload;
        public int page;
        public int page_count;
    }

    [Serializable]
    public class InventoryPayloadDto
    {
        public string id;
        public OrderDto order;
        public PurchasedItemDto item;
        public string created_at;
    }

    [Serializable]
    public class OrderDto
    {
        public string id;
    }

    [Serializable]
    public class PurchasedItemDto
    {
        public string id;
        public UserDto user;
        public string name;
        public string url;
        public MediaFileDto cover;
        public string created_at;
        public VersionDto version;
    }

    [Serializable]
    public class VersionDto
    {
        public string id;
        public string name;
    }

    [Serializable]
    public class UserDto
    {
        public string id;
        public string name;
        public string username;
        public string email;
        public int level;
        public MediaFileDto profile_image;
        public MediaFileDto profile_header;
        public string bio;
    }

    [Serializable]
    public class MediaFileDto
    {
        public string id;
        public string url;
        public string path;
        public string content_type;
        public string extension;
    }

    [Serializable]
    public class GraphQlError
    {
        public string message;
    }
}
#endif
