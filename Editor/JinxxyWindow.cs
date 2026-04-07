#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace DenisHik.JinxxyEditor
{
    public class JinxxyWindow : EditorWindow
    {
        private const string ApiUrl = "https://api.jinxxy.com/graphql";
        private const string SiteUrl = "https://jinxxy.com/";
        private const string Origin = "https://jinxxy.com";
        private const string EditorTitle = "Jinxxy";
        private const string SessionPrefKey = "Unity.Jinxxy.Session";
        private const string SavedAccountFilePath = "UserSettings/JinxxySavedAccount.json";
        private const string LogoAssetPath = "Assets/_DenisHik/JinxxyDownloader/Editor/JinxxyLogo.png";

        private static readonly Vector2 WindowMinSize = new Vector2(760f, 520f);

        private JinxxySessionStore _session;
        private SavedJinxxyAccount _savedAccount;
        private CurrentUser _currentUser;
        private readonly List<InventoryObject> _items = new List<InventoryObject>();
        private readonly Dictionary<string, Texture2D> _textureCache = new Dictionary<string, Texture2D>();

        private Texture2D _logoTexture;
        private GUIStyle _linkStyle;
        private Vector2 _scroll;
        private bool _isBusy;
        private bool _isLoggedIn;
        private bool _triedRestoreSession;
        private string _statusMessage = "Not authorized";
        private string _lastError;
        private bool _showLoginForm;
        private string _loginEmail = string.Empty;
        private string _loginPassword = string.Empty;

        [MenuItem("Tools/Jinxxy/Open Window")]
        public static void OpenWindow()
        {
            var window = GetWindow<JinxxyWindow>();
            window.titleContent = new GUIContent(EditorTitle);
            window.minSize = WindowMinSize;
            window.Show();
        }

        private void OnEnable()
        {
            wantsMouseMove = true;
            titleContent = new GUIContent(EditorTitle);
            minSize = WindowMinSize;
            _logoTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(LogoAssetPath);
            _session = JinxxySessionStore.Load();
            _savedAccount = SavedJinxxyAccount.Load();
            TryRestoreSession();
        }

        private void TryRestoreSession()
        {
            if (_triedRestoreSession)
                return;

            _triedRestoreSession = true;

            if (_session == null || !_session.HasAnyCookie() || string.IsNullOrWhiteSpace(_session.UserId))
            {
                _statusMessage = "Please login";
                return;
            }

            SetBusy(true, "Restoring session...");
            EditorCoroutineUtility.StartCoroutineOwnerless(RestoreSessionRoutine());
        }

        private IEnumerator RestoreSessionRoutine()
        {
            yield return GetUserCardRoutine(_session.UserId, user =>
            {
                _currentUser = user;
                _isLoggedIn = user != null;
                if (user != null)
                {
                    _statusMessage = $"Logged in as {user.Name}";
                    SaveSession();
                }
            }, error =>
            {
                _isLoggedIn = false;
                _statusMessage = "Session expired. Please login again.";
                _lastError = error;
                ClearSession();
            });

            if (_isLoggedIn)
                yield return LoadInventoryRoutine();

            SetBusy(false);
            Repaint();
        }

        private void OnGUI()
        {
            if (!string.IsNullOrWhiteSpace(_lastError))
            {
                DrawPopupMessage(_lastError, MessageType.Error);
            }
            else if (!string.IsNullOrWhiteSpace(_statusMessage))
            {
                DrawPopupMessage(_statusMessage, MessageType.Info);
            }

            if (!_isLoggedIn)
            {
                DrawLoggedOutLogo();
                DrawLoggedOutView();
                DrawLinks();
                return;
            }

            DrawCurrentUserCard();
            EditorGUILayout.Space(10);
            DrawInventoryHeader();
            EditorGUILayout.Space(4);
            DrawInventoryList();
            DrawLinks();
        }

        private void DrawLoggedOutView()
        {
            GUILayout.FlexibleSpace();

            using (new EditorGUILayout.VerticalScope("box"))
            {
                GUILayout.Space(10);
                GUILayout.Label(_showLoginForm ? "Jinxxy Login" : "Jinxxy authorization required", EditorStyles.boldLabel);
                GUILayout.Space(4);
                EditorGUILayout.LabelField(_showLoginForm
                    ? "Enter your email and password."
                    : "Open the login form and enter your email and password.");
                GUILayout.Space(10);

                if (_showLoginForm)
                {
                    DrawInlineLoginForm();
                }
                else
                {
                    GUI.enabled = !_isBusy;
                    if (GUILayout.Button("Open Login", GUILayout.Height(32)))
                    {
                        OpenLoginPopup();
                    }
                    GUI.enabled = true;

                    if (_savedAccount != null && _savedAccount.HasLogin())
                    {
                        GUILayout.Space(10);
                        DrawSavedAccountLoginOffer();
                    }
                }

                GUILayout.Space(4);
            }

            GUILayout.FlexibleSpace();
        }

        private void DrawLoggedOutLogo()
        {
            if (_logoTexture == null || Event.current.type != EventType.Repaint)
                return;

            const float maxWidth = 260f;
            const float maxHeight = 92f;
            const float topOffset = 54f;

            Rect rect = new Rect((position.width - maxWidth) * 0.5f, topOffset, maxWidth, maxHeight);

            GUI.DrawTexture(rect, _logoTexture, ScaleMode.ScaleToFit, true);
        }

        private void DrawInlineLoginForm()
        {
            EditorGUILayout.LabelField("Email");
            _loginEmail = EditorGUILayout.TextField(_loginEmail);

            GUILayout.Space(6);
            EditorGUILayout.LabelField("Password");
            _loginPassword = EditorGUILayout.PasswordField(_loginPassword);

            GUILayout.Space(10);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Back", GUILayout.Height(28)))
                {
                    _showLoginForm = false;
                    GUI.FocusControl(null);
                }

                GUI.enabled = !_isBusy && !string.IsNullOrWhiteSpace(_loginEmail) && !string.IsNullOrWhiteSpace(_loginPassword);
                if (GUILayout.Button("Authorize", GUILayout.Height(28)))
                {
                    SubmitLogin(_loginEmail.Trim(), _loginPassword);
                }
                GUI.enabled = true;
            }
        }

        private void DrawSavedAccountLoginOffer()
        {
            GUILayout.Label("Saved account", EditorStyles.miniBoldLabel);

            Rect rect = GUILayoutUtility.GetRect(280f, 52f, GUILayout.ExpandWidth(false), GUILayout.Height(52f));
            bool previousEnabled = GUI.enabled;
            GUI.enabled = !_isBusy;

            if (GUI.Button(rect, GUIContent.none))
            {
                LoginWithSavedAccount();
            }

            GUI.enabled = previousEnabled;

            var iconTexture = GetCachedTexture(_savedAccount.iconUrl);
            Rect iconRect = new Rect(rect.x + 8f, rect.y + 8f, 36f, 36f);
            DrawCircularTexture(iconRect, iconTexture);

            Rect labelRect = new Rect(iconRect.xMax + 10f, rect.y + 8f, rect.width - 62f, 18f);
            GUI.Label(labelRect, string.IsNullOrWhiteSpace(_savedAccount.name) ? _savedAccount.login : _savedAccount.name, EditorStyles.boldLabel);

            Rect loginRect = new Rect(iconRect.xMax + 10f, rect.y + 28f, rect.width - 62f, 16f);
            GUI.Label(loginRect, _savedAccount.login ?? string.Empty, EditorStyles.miniLabel);
        }

        private void DrawCurrentUserCard()
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var avatarTexture = GetCachedTexture(_currentUser?.AvatarUrl);
                    DrawRoundedAvatar(avatarTexture, 64f);

                    GUILayout.Space(10);

                    using (new EditorGUILayout.VerticalScope())
                    {
                        GUILayout.Space(8);
                        DrawLinkButton(_currentUser?.Name ?? "Unknown", SiteUrl + _currentUser.username);
                        EditorGUILayout.LabelField(_currentUser?.Email ?? "-", EditorStyles.wordWrappedMiniLabel);
                    }

                    GUILayout.FlexibleSpace();

                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(100f)))
                    {
                        GUI.enabled = !_isBusy;

                        if (GUILayout.Button("Refresh", GUILayout.Height(28)))
                        {
                            SetBusy(true, "Refreshing inventory...");
                            EditorCoroutineUtility.StartCoroutineOwnerless(RefreshAllRoutine());
                        }

                        GUILayout.Space(6);

                        if (GUILayout.Button("Logout", GUILayout.Height(28)))
                        {
                            Logout();
                        }

                        GUI.enabled = true;
                    }
                }

                GUILayout.Space(6);
            }
        }

        private void DrawInventoryHeader()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Available objects for install", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                GUILayout.Label($"Count: {_items.Count}", EditorStyles.miniLabel);
            }
        }

        private void DrawInventoryList()
        {
            using (var scrollScope = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = scrollScope.scrollPosition;

                if (_items.Count == 0 && !_isBusy)
                {
                    EditorGUILayout.HelpBox("No inventory items found.", MessageType.Info);
                    return;
                }

                for (int i = 0; i < _items.Count; i++)
                {
                    DrawInventoryItem(_items[i]);
                    GUILayout.Space(8);
                }
            }
        }

        private void DrawInventoryItem(InventoryObject item)
        {
            const float previewSize = 88f;
            const float verticalPadding = 16f;

            EditorGUILayout.BeginVertical("box", GUILayout.Height(previewSize + verticalPadding));
            GUILayout.Space(8);

            using (new EditorGUILayout.HorizontalScope())
            {
                Texture2D preview = GetCachedTexture(item.ImageUrl, item.IsGifImage);
                DrawSquareImage(preview, previewSize, previewSize);

                GUILayout.Space(10);

                using (new EditorGUILayout.VerticalScope())
                {
                    DrawInventoryItemTitle(item);

                    string description = string.IsNullOrWhiteSpace(item.Author)
                        ? item.VersionName
                        : $"Author: {item.Author}" + (string.IsNullOrWhiteSpace(item.VersionName) ? string.Empty : $" | {item.VersionName}");

                    EditorGUILayout.LabelField(description ?? string.Empty, EditorStyles.wordWrappedMiniLabel, GUILayout.ExpandHeight(true));
                    GUILayout.FlexibleSpace();
                }

                GUILayout.Space(10);

                using (new EditorGUILayout.VerticalScope(GUILayout.Width(84f)))
                {
                    GUILayout.FlexibleSpace();

                    GUI.enabled = !_isBusy;
                    if (GUILayout.Button("Files", GUILayout.Width(84f), GUILayout.Height(24f)))
                    {
                        InstallItem(item);
                    }
                    GUI.enabled = true;
                }
            }

            GUILayout.Space(8);
            EditorGUILayout.EndVertical();
        }

        internal void DrawInventoryItemTitle(InventoryObject item)
        {
            DrawLinkButton(item?.Name ?? "Unnamed object", SiteUrl + "my/inventory/" + item.Id);
        }

        private void DrawPopupMessage(string message, MessageType type)
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.HelpBox(message, type);
            }
        }

        private void DrawLinks()
        {
            EnsureLinkStyle();

            EditorGUILayout.BeginHorizontal();

            DrawLinkButton("jinxxy", "https://jinxxy.com/denis_hik", TextAnchor.MiddleCenter);
            DrawLinkButton("Denis Hik", "https://vrchat.denishik.ru/", TextAnchor.MiddleCenter);
            DrawLinkButton("Gumroad", "http://denishik.gumroad.com/", TextAnchor.MiddleCenter);

            EditorGUILayout.EndHorizontal();
            GUILayout.Space(6f);
        }

        private void DrawLinkButton(string label, string url, TextAnchor aligment = TextAnchor.MiddleLeft)
        {
            var content = new GUIContent(label);
            Rect rect = GUILayoutUtility.GetRect(content, EditorStyles.boldLabel, GUILayout.ExpandWidth(true));
            bool canOpen = url != null && !string.IsNullOrWhiteSpace(url);
            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = aligment,
                normal =
                {
                    textColor = Color.white
                },
                hover =
                {
                    textColor = new Color(0.59f, 0.32f, 0.83f),
                },
            };

            if (canOpen)
            {
                EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
                if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
                {
                    Application.OpenURL(url);
                }
            }

            GUI.Label(rect, content, titleStyle);
        }

        private void EnsureLinkStyle()
        {
            if (_linkStyle != null)
                return;

            _linkStyle = new GUIStyle(GUI.skin.button)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                normal =
                {
                    textColor = new Color(0.59f, 0.32f, 0.83f),
                    background = MakeTex(new Color(0f, 0f, 0f, 0f))
                },
                hover =
                {
                    textColor = new Color(0.49f, 0.27f, 0.68f),
                    background = MakeTex(new Color(0f, 0f, 0f, 0f))
                },
                active =
                {
                    textColor = new Color(0.59f, 0.32f, 0.83f),
                    background = MakeTex(new Color(0f, 0f, 0f, 0f))
                },
                border = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(2, 2, 0, 0)
            };
        }

        Texture2D MakeTex(Color color)
        {
            Texture2D texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }
        private void OpenLoginPopup()
        {
            _showLoginForm = true;
        }

        private void CloseLoginPopup()
        {
            _showLoginForm = false;
        }

        internal void SubmitLogin(string email, string password)
        {
            SetBusy(true, "Authorizing...");
            _lastError = null;
            EditorCoroutineUtility.StartCoroutineOwnerless(LoginRoutine(email, password));
        }

        private void LoginWithSavedAccount()
        {
            if (_savedAccount == null || !_savedAccount.TryGetPassword(out string password))
            {
                _lastError = "Saved account password could not be decrypted.";
                _statusMessage = "Please login manually.";
                _loginEmail = _savedAccount?.login ?? string.Empty;
                _loginPassword = string.Empty;
                _showLoginForm = true;
                Repaint();
                return;
            }

            SubmitLogin(_savedAccount.login, password);
        }

        private IEnumerator LoginRoutine(string email, string password)
        {
            yield return LoginRequestRoutine(email, password, user =>
            {
                _currentUser = user;
                _isLoggedIn = user != null;
                _statusMessage = user != null ? $"Logged in as {user.Name}" : "Authorization failed";
                if (user != null)
                {
                    CloseLoginPopup();
                    SaveSavedAccount(email, password, user);
                }
            }, error =>
            {
                _isLoggedIn = false;
                _currentUser = null;
                _lastError = error;
                _statusMessage = "Authorization failed";
            });

            if (_isLoggedIn)
                yield return LoadInventoryRoutine();

            SetBusy(false);
            Repaint();
        }

        private IEnumerator RefreshAllRoutine()
        {
            yield return LoadInventoryRoutine();
            SetBusy(false);
            Repaint();
        }

        private IEnumerator LoadInventoryRoutine()
        {
            yield return GetInventoryRoutine(list =>
            {
                _items.Clear();
                _items.AddRange(list);
                _statusMessage = $"Inventory loaded: {_items.Count}";
            }, error =>
            {
                _lastError = error;
            });
        }

        private void InstallItem(InventoryObject item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id))
                return;

            var window = CreateInstance<InstallFilesWindow>();
            window.Initialize(this, item);
            window.titleContent = new GUIContent("Item's files");
            window.minSize = new Vector2(520f, 260f);
            window.ShowUtility();
        }

        private void Logout()
        {
            _isLoggedIn = false;
            _currentUser = null;
            _items.Clear();
            _lastError = null;
            _statusMessage = "Logged out";
            ClearSession();
            CloseLoginPopup();
            Repaint();
        }

        private void SetBusy(bool busy, string message = null)
        {
            _isBusy = busy;
            if (!string.IsNullOrWhiteSpace(message))
                _statusMessage = message;
            Repaint();
        }

        private void SaveSession()
        {
            if (_session == null)
                _session = new JinxxySessionStore();

            if (_currentUser != null)
            {
                _session.UserId = _currentUser.Id;
                _session.Email = _currentUser.Email;
            }

            _session.Save();
        }

        private void ClearSession()
        {
            _session = new JinxxySessionStore();
            _session.Clear();
            _session.Save();
        }

        private void SaveSavedAccount(string login, string password, CurrentUser user)
        {
            if (user == null || string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password))
                return;

            _savedAccount = SavedJinxxyAccount.Create(user.AvatarUrl, user.Name, login, password);
            _savedAccount.Save();
        }

        private string BuildCookieHeader()
        {
            if (_session == null || _session.Cookies == null || _session.Cookies.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            foreach (var pair in _session.Cookies)
            {
                if (string.IsNullOrWhiteSpace(pair.Name) || string.IsNullOrWhiteSpace(pair.Value))
                    continue;

                if (sb.Length > 0)
                    sb.Append("; ");

                sb.Append(pair.Name);
                sb.Append('=');
                sb.Append(pair.Value);
            }

            return sb.ToString();
        }

        private void MergeResponseCookies(UnityWebRequest request)
        {
            if (_session == null)
                _session = new JinxxySessionStore();

            string singleSetCookie = request.GetResponseHeader("Set-Cookie");
            if (!string.IsNullOrWhiteSpace(singleSetCookie))
                MergeSetCookieHeader(singleSetCookie);

            var headers = request.GetResponseHeaders();
            if (headers == null)
                return;

            foreach (var kv in headers)
            {
                if (string.Equals(kv.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
                    MergeSetCookieHeader(kv.Value);
            }

            SaveSession();
        }

        private void MergeSetCookieHeader(string rawSetCookie)
        {
            if (string.IsNullOrWhiteSpace(rawSetCookie))
                return;

            // Some environments merge multiple Set-Cookie headers into one string.
            // Split only on ", " that are followed by a token and "=".
            string[] possibleCookies = SplitCombinedSetCookie(rawSetCookie);

            foreach (string entry in possibleCookies)
            {
                string firstPart = entry.Split(';')[0].Trim();
                int equalIndex = firstPart.IndexOf('=');
                if (equalIndex <= 0)
                    continue;

                string cookieName = firstPart.Substring(0, equalIndex).Trim();
                string cookieValue = firstPart.Substring(equalIndex + 1).Trim();
                _session.SetCookie(cookieName, cookieValue);
            }
        }

        private static string[] SplitCombinedSetCookie(string raw)
        {
            var result = new List<string>();
            int start = 0;

            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] != ',')
                    continue;

                int next = i + 1;
                while (next < raw.Length && raw[next] == ' ')
                    next++;

                int eq = raw.IndexOf('=', next);
                int semi = raw.IndexOf(';', next);

                if (eq > next && (semi == -1 || eq < semi))
                {
                    result.Add(raw.Substring(start, i - start));
                    start = next;
                }
            }

            result.Add(raw.Substring(start));
            return result.ToArray();
        }

        private IEnumerator LoginRequestRoutine(string email, string password, Action<CurrentUser> onSuccess, Action<string> onFail)
        {
            string payload = BuildLoginPayload(email, password);

            using (var request = CreateGraphQlRequest(payload))
            {
                yield return request.SendWebRequest();
                MergeResponseCookies(request);

                if (!RequestSucceeded(request))
                {
                    onFail?.Invoke(GetTransportError(request));
                    yield break;
                }

                string text = request.downloadHandler.text;
                var response = JsonUtility.FromJson<LoginResponse>(text);

                string apiError = ExtractApiError(response?.errors);
                if (!string.IsNullOrWhiteSpace(apiError))
                {
                    onFail?.Invoke(apiError);
                    yield break;
                }

                UserDto userDto = null;
                if (response?.data != null)
                {
                    userDto = response.data.loginEmail != null
                        ? response.data.loginEmail.user
                        : response.data.user;
                }

                if (userDto == null)
                {
                    onFail?.Invoke("Login succeeded but user payload was empty.");
                    yield break;
                }
                _session.UserId = userDto.id;
                _session.Email = userDto.email;
                SaveSession();

                onSuccess?.Invoke(CurrentUser.FromDto(userDto));
            }
        }

        private IEnumerator GetUserCardRoutine(string userId, Action<CurrentUser> onSuccess, Action<string> onFail)
        {
            string payload = BuildGetUserCardPayload(userId);

            using (var request = CreateGraphQlRequest(payload))
            {
                yield return request.SendWebRequest();
                MergeResponseCookies(request);

                if (!RequestSucceeded(request))
                {
                    onFail?.Invoke(GetTransportError(request));
                    yield break;
                }

                string text = request.downloadHandler.text;
                var response = JsonUtility.FromJson<GetUserCardResponse>(text);

                string apiError = ExtractApiError(response?.errors);
                if (!string.IsNullOrWhiteSpace(apiError))
                {
                    onFail?.Invoke(apiError);
                    yield break;
                }

                if (response?.data?.user == null)
                {
                    onFail?.Invoke("Could not restore user session.");
                    yield break;
                }

                onSuccess?.Invoke(CurrentUser.FromDto(response.data.user));
            }
        }

        private IEnumerator GetInventoryRoutine(Action<List<InventoryObject>> onSuccess, Action<string> onFail)
        {
            var result = new List<InventoryObject>();
            int page = 1;
            int pageCount = 1;

            while (page <= pageCount)
            {
                _statusMessage = $"Loading inventory page {page}/{pageCount}...";
                Repaint();

                string payload = BuildInventoryPayload(page);

                using (var request = CreateGraphQlRequest(payload))
                {
                    yield return request.SendWebRequest();
                    MergeResponseCookies(request);

                    if (!RequestSucceeded(request))
                    {
                        onFail?.Invoke(GetTransportError(request));
                        yield break;
                    }

                    string text = request.downloadHandler.text;
                    var response = JsonUtility.FromJson<InventoryResponse>(text);

                    string apiError = ExtractApiError(response?.errors);
                    if (!string.IsNullOrWhiteSpace(apiError))
                    {
                        onFail?.Invoke(apiError);
                        yield break;
                    }

                    var inventoryItems = response?.data?.inventory_items;
                    if (inventoryItems == null)
                    {
                        onFail?.Invoke("Inventory response was empty.");
                        yield break;
                    }

                    pageCount = Mathf.Max(1, inventoryItems.page_count);

                    if (inventoryItems.payload != null)
                    {
                        foreach (var payloadItem in inventoryItems.payload)
                        {
                            if (payloadItem?.item == null)
                                continue;

                            result.Add(InventoryObject.FromPayload(payloadItem));
                        }
                    }
                }

                page++;
            }

            onSuccess?.Invoke(result);
        }

        private IEnumerator GetInstallFilesRoutine(InventoryObject item, Action<List<ProductDownloadFile>> onSuccess, Action<string> onFail)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id))
            {
                onFail?.Invoke("Inventory item id is empty.");
                yield break;
            }

            string url = SiteUrl + "my/inventory/" + item.Id;
            using (var request = UnityWebRequest.Get(url))
            {
                string cookieHeader = BuildCookieHeader();
                if (!string.IsNullOrWhiteSpace(cookieHeader))
                    request.SetRequestHeader("Cookie", cookieHeader);

                request.SetRequestHeader("Accept", "text/html");
                request.SetRequestHeader("Referer", SiteUrl);

                yield return request.SendWebRequest();
                MergeResponseCookies(request);

                if (!RequestSucceeded(request))
                {
                    onFail?.Invoke(GetTransportError(request));
                    yield break;
                }

                List<ProductDownloadFile> files = ExtractProductFilesFromHtml(request.downloadHandler.text);
                onSuccess?.Invoke(files);
            }
        }

        private IEnumerator DownloadProductFileRoutine(InventoryObject item, ProductDownloadFile file, Action<string> onSuccess, Action<string> onFail, Action<float> onProgress = null)
        {
            if (item == null || file == null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(file.Id))
            {
                onFail?.Invoke("Download data is empty.");
                yield break;
            }

            string payload = BuildDownloadFilePayload(item.Id, file.Id);
            using (var request = CreateGraphQlRequest(payload))
            {
                yield return request.SendWebRequest();
                MergeResponseCookies(request);

                if (!RequestSucceeded(request))
                {
                    onFail?.Invoke(GetTransportError(request));
                    yield break;
                }

                var response = JsonUtility.FromJson<DownloadFileResponse>(request.downloadHandler.text);
                string apiError = ExtractApiError(response?.errors);
                if (!string.IsNullOrWhiteSpace(apiError))
                {
                    onFail?.Invoke(apiError);
                    yield break;
                }

                string downloadUrl = response?.data?.inventory_item?.downloadFile;
                if (string.IsNullOrWhiteSpace(downloadUrl))
                {
                    onFail?.Invoke("Download URL was empty.");
                    yield break;
                }

                yield return DownloadProductFileContentRoutine(downloadUrl, item, file, onSuccess, onFail, onProgress);
            }
        }

        private IEnumerator DownloadProductFileContentRoutine(string downloadUrl, InventoryObject item, ProductDownloadFile file, Action<string> onSuccess, Action<string> onFail, Action<float> onProgress)
        {
            using (var request = UnityWebRequest.Get(downloadUrl))
            {
                string cookieHeader = BuildCookieHeader();
                if (!string.IsNullOrWhiteSpace(cookieHeader))
                    request.SetRequestHeader("Cookie", cookieHeader);

                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    onProgress?.Invoke(request.downloadProgress);
                    yield return null;
                }

                onProgress?.Invoke(1f);

                if (!RequestSucceeded(request))
                {
                    onFail?.Invoke(GetTransportError(request));
                    yield break;
                }

                string directory = GetProductDownloadDirectory(item);
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                string extension = GetProductFileExtension(file);
                string downloadPath = Path.Combine(directory, EnsureDownloadFileName(file?.FileName, extension));
                File.WriteAllBytes(downloadPath, request.downloadHandler.data);

                if (string.Equals(extension, "zip", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractZipToDirectory(downloadPath, directory);
                    AssetDatabase.Refresh();
                    RevealPathInProject(directory);
                    onSuccess?.Invoke(directory);
                    yield break;
                }

                if (string.Equals(extension, "unitypackage", StringComparison.OrdinalIgnoreCase))
                {
                    AssetDatabase.ImportPackage(downloadPath, true);
                    RevealPathInProject(downloadPath);
                    onSuccess?.Invoke(downloadPath);
                    yield break;
                }

                onFail?.Invoke("Unsupported file extension.");
            }
        }

        private UnityWebRequest CreateGraphQlRequest(string jsonPayload)
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
            var request = new UnityWebRequest(ApiUrl, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Accept", "application/json");
            request.SetRequestHeader("Origin", Origin);
            request.SetRequestHeader("Referer", SiteUrl);

            string cookieHeader = BuildCookieHeader();
            if (!string.IsNullOrWhiteSpace(cookieHeader))
                request.SetRequestHeader("Cookie", cookieHeader);

            return request;
        }

        private bool RequestSucceeded(UnityWebRequest request)
        {
#if UNITY_2020_2_OR_NEWER
            return request.result != UnityWebRequest.Result.ConnectionError &&
                   request.result != UnityWebRequest.Result.ProtocolError;
#else
            return !request.isHttpError && !request.isNetworkError;
#endif
        }

        private string GetTransportError(UnityWebRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.error))
                return request.error;

            return $"HTTP {(long)request.responseCode}";
        }

        private static string ExtractApiError(GraphQlError[] errors)
        {
            if (errors == null || errors.Length == 0)
                return null;

            return string.IsNullOrWhiteSpace(errors[0].message)
                ? "Unknown API error."
                : errors[0].message;
        }

        private string BuildLoginPayload(string email, string password)
        {
            string query = "fragment authUserInfo on AuthUser {\\n  id\\n  name\\n  username\\n  email\\n  badges\\n  tags\\n  level\\n  dob\\n  over_18\\n  profile_image {\\n    id\\n    url\\n    path\\n    content_type\\n    extension\\n    __typename\\n  }\\n  stores {\\n    id\\n    name\\n    store_url\\n    external_service\\n    updated_at\\n    __typename\\n  }\\n  __typename\\n}\\n\\nmutation loginEmail($email: String!, $password: String!) {\\n  loginEmail(input: {email: $email, password: $password}) {\\n    user {\\n      ...authUserInfo\\n      __typename\\n    }\\n    __typename\\n  }\\n}";
            return
                "{"
                + "\"operationName\":\"loginEmail\","
                + "\"variables\":{"
                + $"\"email\":{ToJsonString(email)},"
                + $"\"password\":{ToJsonString(password)}"
                + "},"
                + "\"extensions\":{"
                + "\"clientLibrary\":{"
                + "\"name\":\"@apollo/client\","
                + "\"version\":\"4.0.5\""
                + "}"
                + "},"
                + $"\"query\":\"{query}\""
                + "}";
        }

        private string BuildGetUserCardPayload(string id)
        {
            string query = "query getUserCard($id: ID!) {\\n  user(id: $id) {\\n    id\\n    name\\n    username\\n    email\\n    bio\\n    level\\n    profile_image {\\n      id\\n      url\\n      path\\n      content_type\\n      extension\\n      __typename\\n    }\\n    profile_header {\\n      id\\n      url\\n      path\\n      content_type\\n      extension\\n      __typename\\n    }\\n    relationship {\\n      id\\n      target_id\\n      user_id\\n      user_following\\n      user_blocked\\n      target_blocked\\n      target_following\\n      __typename\\n    }\\n    __typename\\n  }\\n}";
            return
                "{"
                + "\"operationName\":\"getUserCard\","
                + "\"variables\":{"
                + $"\"id\":{ToJsonString(id)}"
                + "},"
                + "\"extensions\":{"
                + "\"clientLibrary\":{"
                + "\"name\":\"@apollo/client\","
                + "\"version\":\"4.0.5\""
                + "}"
                + "},"
                + $"\"query\":\"{query}\""
                + "}";
        }

        private string BuildInventoryPayload(int page)
        {
            string query = "query getInventoryItems($input: InventoryItemsInput) {\\n  inventory_items(input: $input) {\\n    payload {\\n      id\\n      order {\\n        id\\n        __typename\\n      }\\n      item {\\n        id\\n        user {\\n          id\\n          username\\n          badges\\n          profile_image {\\n            id\\n            url\\n            content_type\\n            __typename\\n          }\\n          __typename\\n        }\\n        name\\n        url\\n        cover {\\n          id\\n          url\\n          content_type\\n          extension\\n          __typename\\n        }\\n        created_at\\n        version {\\n          id\\n          name\\n          __typename\\n        }\\n        __typename\\n      }\\n      created_at\\n      __typename\\n    }\\n    page\\n    page_count\\n    __typename\\n  }\\n}";
            return
                "{"
                + "\"operationName\":\"getInventoryItems\","
                + "\"variables\":{"
                + "\"input\":{"
                + $"\"page\":{page}"
                + "}"
                + "},"
                + "\"extensions\":{"
                + "\"clientLibrary\":{"
                + "\"name\":\"@apollo/client\","
                + "\"version\":\"4.0.5\""
                + "}"
                + "},"
                + $"\"query\":\"{query}\""
                + "}";
        }

        private string BuildDownloadFilePayload(string inventoryId, string fileId)
        {
            string query = "mutation downloadFile($inventory_id: ID!, $file_id: ID!) {\\n  inventory_item(id: $inventory_id) {\\n    downloadFile(inventory_id: $inventory_id, file_id: $file_id)\\n    __typename\\n  }\\n}";
            return
                "{"
                + "\"operationName\":\"downloadFile\","
                + "\"variables\":{"
                + $"\"inventory_id\":{ToJsonString(inventoryId)},"
                + $"\"file_id\":{ToJsonString(fileId)}"
                + "},"
                + "\"extensions\":{"
                + "\"clientLibrary\":{"
                + "\"name\":\"@apollo/client\","
                + "\"version\":\"4.0.5\""
                + "}"
                + "},"
                + $"\"query\":\"{query}\""
                + "}";
        }

        private static string ToJsonString(string value)
        {
            if (value == null)
                return "null";

            return "\"" + value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t") + "\"";
        }

        private static List<ProductDownloadFile> ExtractProductFilesFromHtml(string html)
        {
            var files = new List<ProductDownloadFile>();
            if (string.IsNullOrWhiteSpace(html))
                return files;

            string text = Regex.Unescape(WebUtility.HtmlDecode(html));
            var seenIds = new HashSet<string>();
            MatchCollection matches = Regex.Matches(
                text,
                "\\{[^{}]*\"__typename\"\\s*:\\s*\"ProductFile\"[^{}]*\"id\"\\s*:\\s*\"(?<id>[^\"]+)\"[^{}]*\"file_name\"\\s*:\\s*\"(?<name>(?:\\\\.|[^\"])*)\"[^{}]*\"extension\"\\s*:\\s*\"(?<extension>[^\"]*)\"[^{}]*\\}",
                RegexOptions.Singleline);

            foreach (Match match in matches)
            {
                string id = match.Groups["id"].Value;
                string fileName = Regex.Unescape(match.Groups["name"].Value);
                string extension = match.Groups["extension"].Value;
                if (string.IsNullOrWhiteSpace(id) || !seenIds.Add(id))
                    continue;

                files.Add(new ProductDownloadFile
                {
                    Id = id,
                    FileName = string.IsNullOrWhiteSpace(fileName) ? id : fileName,
                    Extension = extension
                });
            }

            return files;
        }

        private static string EnsureDownloadFileName(string fileName, string extension)
        {
            string safeName = SanitizeFileName(string.IsNullOrWhiteSpace(fileName) ? "JinxxyPackage" : fileName);
            string normalizedExtension = string.IsNullOrWhiteSpace(extension)
                ? ".unitypackage"
                : "." + extension.TrimStart('.');

            return Path.ChangeExtension(safeName, normalizedExtension);
        }

        private static string GetProductDownloadDirectory(InventoryObject item)
        {
            string productName = SanitizeFileName(string.IsNullOrWhiteSpace(item?.Name) ? item?.Id : item.Name);
            return Path.Combine(Directory.GetCurrentDirectory(), "Assets", "_DenisHik", "JinxxyDownloader", "assets", productName);
        }

        private static void RevealPathInProject(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
                return;

            string projectRoot = Path.GetFullPath(Directory.GetCurrentDirectory());
            string fullPath = Path.GetFullPath(absolutePath);
            if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                return;

            string relativePath = fullPath.Substring(projectRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            relativePath = relativePath.Replace('\\', '/');

            AssetDatabase.Refresh();
            UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(relativePath);
            if (asset == null)
                return;

            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        private static string SanitizeFileName(string fileName)
        {
            string safeName = string.IsNullOrWhiteSpace(fileName) ? "Jinxxy" : fileName;
            foreach (char invalidChar in Path.GetInvalidFileNameChars())
                safeName = safeName.Replace(invalidChar, '_');

            return safeName.Trim();
        }

        private static string GetProductFileExtension(ProductDownloadFile file)
        {
            string extension = file?.Extension;
            if (string.IsNullOrWhiteSpace(extension))
                extension = Path.GetExtension(file?.FileName);

            return extension?.TrimStart('.') ?? string.Empty;
        }

        private static void ExtractZipToDirectory(string zipPath, string directory)
        {
            string fullDirectory = Path.GetFullPath(directory);
            if (!fullDirectory.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                fullDirectory += Path.DirectorySeparatorChar;

            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string destinationPath = Path.GetFullPath(Path.Combine(fullDirectory, entry.FullName));
                    if (!destinationPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destinationPath);
                        continue;
                    }

                    string destinationDirectory = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrWhiteSpace(destinationDirectory) && !Directory.Exists(destinationDirectory))
                        Directory.CreateDirectory(destinationDirectory);

                    entry.ExtractToFile(destinationPath, true);
                }
            }
        }

        private static bool CanDownloadProductFile(ProductDownloadFile file)
        {
            if (file == null)
                return false;

            string extension = GetProductFileExtension(file);
            return string.Equals(extension, "zip", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, "unitypackage", StringComparison.OrdinalIgnoreCase);
        }

        private Texture2D GetCachedTexture(string url, bool isGif = false)
        {
            if (string.IsNullOrWhiteSpace(url))
                return Texture2D.grayTexture;

            if (_textureCache.TryGetValue(url, out var cached) && cached != null)
                return cached;

            _textureCache[url] = Texture2D.grayTexture;
            EditorCoroutineUtility.StartCoroutineOwnerless(LoadTextureRoutine(url, isGif));
            return Texture2D.grayTexture;
        }

        private IEnumerator LoadTextureRoutine(string url, bool isGif)
        {
            if (string.IsNullOrWhiteSpace(url))
                yield break;

            bool shouldLoadAsGif = isGif || IsGifUrl(url);
            if (shouldLoadAsGif)
            {
                using (var request = UnityWebRequest.Get(url))
                {
                    string cookieHeader = BuildCookieHeader();
                    if (!string.IsNullOrWhiteSpace(cookieHeader))
                        request.SetRequestHeader("Cookie", cookieHeader);

                    yield return request.SendWebRequest();

                    if (!RequestSucceeded(request))
                        yield break;

                    if (TryDecodeFirstGifFrame(request.downloadHandler.data, out var gifTexture))
                    {
                        _textureCache[url] = gifTexture;
                        Repaint();
                    }
                }

                yield break;
            }

            using (var request = UnityWebRequestTexture.GetTexture(url))
            {
                string cookieHeader = BuildCookieHeader();
                if (!string.IsNullOrWhiteSpace(cookieHeader))
                    request.SetRequestHeader("Cookie", cookieHeader);

                yield return request.SendWebRequest();

                if (!RequestSucceeded(request))
                    yield break;

                var texture = DownloadHandlerTexture.GetContent(request);
                if (texture != null)
                {
                    _textureCache[url] = texture;
                    Repaint();
                }
            }
        }

        private static bool IsGifUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            int queryIndex = url.IndexOf('?');
            string path = queryIndex >= 0 ? url.Substring(0, queryIndex) : url;
            return path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGifMedia(string contentType, string extension)
        {
            return string.Equals(extension, "gif", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase) ||
                   (!string.IsNullOrWhiteSpace(contentType) && contentType.StartsWith("image/gif", StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryDecodeFirstGifFrame(byte[] data, out Texture2D texture)
        {
            texture = null;

            if (data == null || data.Length < 13)
                return false;

            if (data[0] != 0x47 || data[1] != 0x49 || data[2] != 0x46)
                return false;

            int index = 6;
            int canvasWidth = ReadUInt16(data, index);
            int canvasHeight = ReadUInt16(data, index + 2);
            index += 4;

            byte packed = data[index++];
            int backgroundIndex = data[index++];
            index++;

            Color32[] globalColorTable = null;
            if ((packed & 0x80) != 0)
            {
                int colorCount = 1 << ((packed & 0x07) + 1);
                if (!TryReadColorTable(data, ref index, colorCount, out globalColorTable))
                    return false;
            }

            int transparentIndex = -1;

            while (index < data.Length)
            {
                byte blockType = data[index++];

                if (blockType == 0x3B)
                    return false;

                if (blockType == 0x21)
                {
                    if (index >= data.Length)
                        return false;

                    byte label = data[index++];
                    if (label == 0xF9 && index < data.Length)
                    {
                        int blockSize = data[index++];
                        if (blockSize == 4 && index + 4 <= data.Length)
                        {
                            byte graphicControl = data[index++];
                            index += 2;
                            int candidateTransparentIndex = data[index++];
                            if ((graphicControl & 0x01) != 0)
                                transparentIndex = candidateTransparentIndex;
                            index++;
                            continue;
                        }

                        index += Math.Max(0, blockSize);
                    }

                    if (!SkipSubBlocks(data, ref index))
                        return false;

                    continue;
                }

                if (blockType != 0x2C)
                    return false;

                if (index + 9 > data.Length)
                    return false;

                int left = ReadUInt16(data, index);
                int top = ReadUInt16(data, index + 2);
                int width = ReadUInt16(data, index + 4);
                int height = ReadUInt16(data, index + 6);
                index += 8;

                byte imagePacked = data[index++];
                bool hasLocalColorTable = (imagePacked & 0x80) != 0;
                bool isInterlaced = (imagePacked & 0x40) != 0;

                Color32[] colorTable = globalColorTable;
                if (hasLocalColorTable)
                {
                    int colorCount = 1 << ((imagePacked & 0x07) + 1);
                    if (!TryReadColorTable(data, ref index, colorCount, out colorTable))
                        return false;
                }

                if (colorTable == null || index >= data.Length)
                    return false;

                int lzwMinCodeSize = data[index++];
                if (!TryReadSubBlocks(data, ref index, out var compressedData))
                    return false;

                if (!TryDecodeGifLzw(compressedData, lzwMinCodeSize, width * height, out var indices))
                    return false;

                var pixels = new Color32[canvasWidth * canvasHeight];
                Color32 background = backgroundIndex >= 0 && backgroundIndex < colorTable.Length
                    ? colorTable[backgroundIndex]
                    : new Color32(0, 0, 0, 0);

                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = background;

                FillGifPixels(pixels, canvasWidth, canvasHeight, left, top, width, height, colorTable, indices, transparentIndex, isInterlaced);

                texture = new Texture2D(canvasWidth, canvasHeight, TextureFormat.RGBA32, false);
                texture.SetPixels32(pixels);
                texture.Apply();
                return true;
            }

            return false;
        }

        private static int ReadUInt16(byte[] data, int index)
        {
            return data[index] | (data[index + 1] << 8);
        }

        private static bool TryReadColorTable(byte[] data, ref int index, int colorCount, out Color32[] colors)
        {
            colors = null;
            int byteCount = colorCount * 3;
            if (index + byteCount > data.Length)
                return false;

            colors = new Color32[colorCount];
            for (int i = 0; i < colorCount; i++)
            {
                colors[i] = new Color32(data[index], data[index + 1], data[index + 2], 255);
                index += 3;
            }

            return true;
        }

        private static bool SkipSubBlocks(byte[] data, ref int index)
        {
            while (index < data.Length)
            {
                int size = data[index++];
                if (size == 0)
                    return true;

                index += size;
                if (index > data.Length)
                    return false;
            }

            return false;
        }

        private static bool TryReadSubBlocks(byte[] data, ref int index, out byte[] bytes)
        {
            bytes = null;
            var result = new List<byte>();

            while (index < data.Length)
            {
                int size = data[index++];
                if (size == 0)
                {
                    bytes = result.ToArray();
                    return true;
                }

                if (index + size > data.Length)
                    return false;

                for (int i = 0; i < size; i++)
                    result.Add(data[index + i]);

                index += size;
            }

            return false;
        }

        private static bool TryDecodeGifLzw(byte[] data, int minCodeSize, int expectedSize, out List<int> output)
        {
            output = new List<int>(expectedSize);

            if (data == null || minCodeSize < 2 || minCodeSize > 8)
                return false;

            int clearCode = 1 << minCodeSize;
            int endCode = clearCode + 1;
            int codeSize = minCodeSize + 1;
            int nextCode = endCode + 1;
            int bitPosition = 0;
            List<int> previous = null;

            var dictionary = CreateGifDictionary(clearCode);

            while (TryReadGifCode(data, ref bitPosition, codeSize, out int code))
            {
                if (code == clearCode)
                {
                    dictionary = CreateGifDictionary(clearCode);
                    codeSize = minCodeSize + 1;
                    nextCode = endCode + 1;
                    previous = null;
                    continue;
                }

                if (code == endCode)
                    break;

                List<int> entry;
                if (code < dictionary.Count && dictionary[code] != null)
                {
                    entry = new List<int>(dictionary[code]);
                }
                else if (code == nextCode && previous != null)
                {
                    entry = new List<int>(previous);
                    entry.Add(previous[0]);
                }
                else
                {
                    return false;
                }

                output.AddRange(entry);
                if (output.Count >= expectedSize)
                    return true;

                if (previous != null && nextCode < 4096)
                {
                    var nextEntry = new List<int>(previous);
                    nextEntry.Add(entry[0]);

                    while (dictionary.Count <= nextCode)
                        dictionary.Add(null);

                    dictionary[nextCode++] = nextEntry;
                    if (nextCode == (1 << codeSize) && codeSize < 12)
                        codeSize++;
                }

                previous = entry;
            }

            return output.Count > 0;
        }

        private static List<List<int>> CreateGifDictionary(int clearCode)
        {
            var dictionary = new List<List<int>>(4096);
            for (int i = 0; i < clearCode; i++)
                dictionary.Add(new List<int> { i });

            dictionary.Add(null);
            dictionary.Add(null);
            return dictionary;
        }

        private static bool TryReadGifCode(byte[] data, ref int bitPosition, int codeSize, out int code)
        {
            code = 0;

            if (bitPosition + codeSize > data.Length * 8)
                return false;

            for (int i = 0; i < codeSize; i++)
            {
                int byteIndex = (bitPosition + i) / 8;
                int bitIndex = (bitPosition + i) % 8;
                if ((data[byteIndex] & (1 << bitIndex)) != 0)
                    code |= 1 << i;
            }

            bitPosition += codeSize;
            return true;
        }

        private static void FillGifPixels(Color32[] pixels, int canvasWidth, int canvasHeight, int left, int top, int width, int height, Color32[] colorTable, List<int> indices, int transparentIndex, bool isInterlaced)
        {
            int sourceIndex = 0;

            for (int pass = 0; pass < (isInterlaced ? 4 : 1); pass++)
            {
                int row = isInterlaced ? GetInterlaceStartRow(pass) : 0;
                int rowStep = isInterlaced ? GetInterlaceRowStep(pass) : 1;

                for (; row < height; row += rowStep)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (sourceIndex >= indices.Count)
                            return;

                        int colorIndex = indices[sourceIndex++];
                        if (colorIndex == transparentIndex || colorIndex < 0 || colorIndex >= colorTable.Length)
                            continue;

                        int targetX = left + x;
                        int targetY = canvasHeight - 1 - (top + row);
                        if (targetX < 0 || targetX >= canvasWidth || targetY < 0 || targetY >= canvasHeight)
                            continue;

                        pixels[targetY * canvasWidth + targetX] = colorTable[colorIndex];
                    }
                }
            }
        }

        private static int GetInterlaceStartRow(int pass)
        {
            switch (pass)
            {
                case 0:
                    return 0;
                case 1:
                    return 4;
                case 2:
                    return 2;
                default:
                    return 1;
            }
        }

        private static int GetInterlaceRowStep(int pass)
        {
            switch (pass)
            {
                case 0:
                case 1:
                    return 8;
                case 2:
                    return 4;
                default:
                    return 2;
            }
        }

        private void DrawSquareImage(Texture2D texture, float width, float height)
        {
            Rect rect = GUILayoutUtility.GetRect(width, height, GUILayout.Width(width), GUILayout.Height(height));
            if (Event.current.type == EventType.Repaint)
                GUI.DrawTexture(rect, texture ?? Texture2D.grayTexture, ScaleMode.ScaleAndCrop);
        }

        private void DrawRoundedAvatar(Texture2D texture, float size)
        {
            Rect rect = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
            texture = texture ?? Texture2D.grayTexture;

            if (Event.current.type == EventType.Repaint)
            {
                GUI.BeginGroup(rect);
                Rect localRect = new Rect(0f, 0f, rect.width, rect.height);

                // Simple faux-rounded avatar: clip by drawing into a rounded box area.
                GUI.Box(localRect, GUIContent.none);
                Rect inner = new Rect(2f, 2f, localRect.width - 4f, localRect.height - 4f);
                GUI.DrawTexture(inner, texture, ScaleMode.ScaleAndCrop);
                GUI.EndGroup();
            }
        }

        private static void DrawCircularTexture(Rect rect, Texture2D texture)
        {
            if (Event.current.type != EventType.Repaint)
                return;

            texture = texture ?? Texture2D.grayTexture;
            GUI.BeginGroup(rect);
            Rect localRect = new Rect(0f, 0f, rect.width, rect.height);
            GUI.Box(localRect, GUIContent.none);
            GUI.DrawTexture(new Rect(2f, 2f, rect.width - 4f, rect.height - 4f), texture, ScaleMode.ScaleAndCrop);
            GUI.EndGroup();
        }

        public class InstallFilesWindow : EditorWindow
        {
            private JinxxyWindow _owner;
            private InventoryObject _item;
            private readonly List<ProductDownloadFile> _files = new List<ProductDownloadFile>();
            private Vector2 _scroll;
            private bool _isLoading;
            private bool _isDownloading;
            private float _downloadProgress;
            private string _status;
            private string _error;

            internal void Initialize(JinxxyWindow owner, InventoryObject item)
            {
                _owner = owner;
                _item = item;
                _status = "Loading files...";
                wantsMouseMove = true;
                Reload();
            }

            private void OnGUI()
            {
                GUILayout.Space(10);
                if (_owner != null)
                    _owner.DrawInventoryItemTitle(_item);
                else
                    GUILayout.Label(_item?.Name ?? "Jinxxy Product", EditorStyles.boldLabel);
                GUILayout.Space(8);

                if (!string.IsNullOrWhiteSpace(_error))
                    EditorGUILayout.HelpBox(_error, MessageType.Error);
                else if (!string.IsNullOrWhiteSpace(_status))
                    EditorGUILayout.HelpBox(_status, MessageType.Info);

                if (_isDownloading)
                {
                    Rect progressRect = GUILayoutUtility.GetRect(1f, 18f, GUILayout.ExpandWidth(true));
                    EditorGUI.ProgressBar(progressRect, Mathf.Clamp01(_downloadProgress), $"{Mathf.RoundToInt(_downloadProgress * 100f)}%");
                    GUILayout.Space(4);
                }

                using (var scrollScope = new EditorGUILayout.ScrollViewScope(_scroll))
                {
                    _scroll = scrollScope.scrollPosition;

                    if (_files.Count == 0 && !_isLoading)
                    {
                        EditorGUILayout.HelpBox("No ProductFile entries found.", MessageType.Info);
                    }

                    for (int i = 0; i < _files.Count; i++)
                        DrawFileRow(_files[i]);
                }

                GUILayout.Space(8);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    GUI.enabled = !_isLoading && !_isDownloading;
                    if (GUILayout.Button("Refresh", GUILayout.Width(90), GUILayout.Height(26)))
                        Reload();
                    GUI.enabled = true;
                }
            }

            private void DrawFileRow(ProductDownloadFile file)
            {
                using (new EditorGUILayout.HorizontalScope("box"))
                {
                    GUILayout.Label(file.FileName ?? file.Id, EditorStyles.wordWrappedLabel);
                    GUILayout.FlexibleSpace();

                    bool canDownload = CanDownloadProductFile(file);
                    if (!canDownload)
                        GUILayout.Label("Unsupported", EditorStyles.miniLabel, GUILayout.Width(78f));

                    GUI.enabled = !_isLoading && !_isDownloading && canDownload;
                    if (GUILayout.Button("Download", GUILayout.Width(90), GUILayout.Height(24)))
                        Download(file);
                    GUI.enabled = true;
                }
            }

            private void Reload()
            {
                if (_owner == null || _item == null)
                    return;

                _isLoading = true;
                _downloadProgress = 0f;
                _error = null;
                _status = "Loading files...";
                _files.Clear();
                Repaint();

                EditorCoroutineUtility.StartCoroutineOwnerless(_owner.GetInstallFilesRoutine(_item, files =>
                {
                    _files.Clear();
                    if (files != null)
                        _files.AddRange(files);

                    _isLoading = false;
                    _status = $"Files loaded: {_files.Count}";
                    Repaint();
                }, error =>
                {
                    _isLoading = false;
                    _error = error;
                    _status = null;
                    Repaint();
                }));
            }

            private void Download(ProductDownloadFile file)
            {
                if (_owner == null || _item == null || file == null)
                    return;

                _isDownloading = true;
                _downloadProgress = 0f;
                _error = null;
                _status = $"Downloading {file.FileName}...";
                Repaint();

                EditorCoroutineUtility.StartCoroutineOwnerless(_owner.DownloadProductFileRoutine(_item, file, path =>
                {
                    _isDownloading = false;
                    _downloadProgress = 1f;
                    _status = $"Downloaded: {path}";
                    Repaint();
                }, error =>
                {
                    _isDownloading = false;
                    _error = error;
                    _status = null;
                    Repaint();
                }, progress =>
                {
                    _downloadProgress = Mathf.Clamp01(progress);
                    _status = $"Downloading {file.FileName}... {Mathf.RoundToInt(_downloadProgress * 100f)}%";
                    Repaint();
                }));
            }
        }

    }
}
#else
using UnityEngine;

public class JinxxyWindow_EditorOnly : MonoBehaviour
{
    private void Awake()
    {
        Debug.LogWarning("JinxxyWindow.cs is an editor-only script. Put it inside an Editor folder.");
    }
}
#endif
