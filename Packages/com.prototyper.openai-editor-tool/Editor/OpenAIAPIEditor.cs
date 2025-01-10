using UnityEngine;
using UnityEditor;

using System;
using System.Collections.Generic;
using System.Linq;

using OpenAI;
using OpenAI.Threads;
using OpenAI.Assistants;
using OpenAI.Models;
using OpenAI.Files;
using OpenAI.VectorStores;
using Tool = OpenAI.Tool;
using OpenAI.FineTuning;

using System.Threading.Tasks;
using System.Threading;
using System.Text;
using System.IO;
using Utilities.WebRequestRest;
// IServerSentEvent
using Utilities.WebRequestRest.Interfaces;

using Newtonsoft.Json;
using PlasticGui.WebApi.Responses;

namespace SS
{
    public class OpenAIAPIEditor : EditorWindow
    {

        #region General

        private enum MessageRole
        {
            Assistant = 2,
            User = 3,
        }

        private enum Page
        {
            Threads,
            Assistants,
            Files,
            FineTune,
            EnvironmentVariables,
        }

        [MenuItem("Tools/SS/OpenAI/API Editor")]
        public static void OpenEditor()
        {
            var window = GetWindow<OpenAIAPIEditor>();
            window.titleContent = new GUIContent("OpenAI API Editor");
            window.Show();
        }
        private Page currentPage = Page.Threads;
        private OpenAIClient client = null;
        private CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

        private void OnDrawAutherization()
        {
            // API Key
            EditorGUILayout.BeginHorizontal();
            {
                apiKey = EditorGUILayout.PasswordField("API Key", apiKey, GUILayout.Width(300));
                var backupGUIEnabled = GUI.enabled;
                GUI.enabled = client == null || client.HasValidAuthentication == false;
                if (GUILayout.Button("Connect", GUILayout.Width(100)))
                {
                    cancellationTokenSource = new CancellationTokenSource();
                    Connect();
                }
                GUI.enabled = !GUI.enabled;
                if (GUILayout.Button("Disconnect", GUILayout.Width(100)))
                {
                    client = null;
                    cancellationTokenSource?.Cancel();
                }
                GUI.enabled = backupGUIEnabled;
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawPageToggles()
        {
            var backupBGColor = GUI.backgroundColor;
            // Toolbar - Draw pages
            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            {
                var pageValues = (Page[])Enum.GetValues(typeof(Page));
                foreach (var page in pageValues)
                {
                    GUI.backgroundColor = currentPage == page ? Color.green : backupBGColor;
                    if (GUILayout.Button(page.ToString(), EditorStyles.toolbarButton))
                    {
                        currentPage = page;
                    }
                    GUI.backgroundColor = backupBGColor;
                }
                // Draw a docucment icon button (no text)
                if (GUILayout.Button(EditorGUIUtility.IconContent("_Help"), GUILayout.Width(30)))
                {
                    Application.OpenURL("https://beta.openai.com/docs/");
                }
            }
            GUILayout.EndHorizontal();
        }

        private void OnGUI()
        {
            OnDrawAutherization();
            DrawPageToggles();

            switch (currentPage)
            {
                case Page.Threads:
                    OnDrawThreadsAndMessages();
                    break;
                case Page.Assistants:
                    OnDrawAssistants();
                    break;
                case Page.EnvironmentVariables:
                    OnDrawEnvironmentVariables();
                    break;
                case Page.Files:
                    OnDrawFiles();
                    break;
                case Page.FineTune:
                    OnDrawFineTune();
                    break;
            }
        }

        private void DrawEnvironmentVariable(string key, string defaultValue = "")
        {
            // if not exist, press + button to create.
            // or edit here.
            var value = Environment.GetEnvironmentVariable(key);
            GUILayout.BeginHorizontal();
            {
                GUILayout.Label(key, GUILayout.Width(100));
                if (value == null)
                {
                    if (GUILayout.Button("+", GUILayout.Width(20)))
                    {
                        Environment.SetEnvironmentVariable(key, defaultValue);
                    }
                }
                else
                {
                    EditorGUI.BeginChangeCheck();
                    value = EditorGUILayout.DelayedTextField(value);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Environment.SetEnvironmentVariable(key, value);
                    }
                    if (GUILayout.Button("-", GUILayout.Width(20)))
                    {
                        Environment.SetEnvironmentVariable(key, null);
                    }
                }
            }
            GUILayout.EndHorizontal();
        }

        private void OnDrawEnvironmentVariables()
        {
            // Use your system's environment variables specify an api key and organization to use.
            // Use OPENAI_API_KEY for your api key.
            // Use OPENAI_ORGANIZATION_ID to specify an organization.
            // Use OPENAI_PROJECT_ID to specify a project.

            // Environment variable editor
            GUILayout.Label("Environment Variables", EditorStyles.boldLabel);
            DrawEnvironmentVariable("OPENAI_API_KEY", "sk-proj-abc123");
            DrawEnvironmentVariable("OPENAI_ORGANIZATION_ID", "optional");
            DrawEnvironmentVariable("OPENAI_PROJECT_ID", "optional");
        }

        private void Connect()
        {
            if (client == null || client.HasValidAuthentication == false)
            {
                if (string.IsNullOrEmpty(apiKey))
                {
                    apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                }
                if (string.IsNullOrEmpty(apiKey))
                {
                    LogError("API Key is required");
                    return;
                }
                var authentication = new OpenAIAuthentication(apiKey);
                var configuration = new OpenAIConfiguration();
                var settings = new OpenAISettings(configuration);
                client = new OpenAIClient(authentication, settings);
            }
        }
        #endregion

        #region GUI
        private GUIStyle _style;
        private GUIStyle style
        {
            get
            {
                {
                    _style = new GUIStyle(EditorStyles.textArea);
                    _style.richText = true;
                    _style.normal.textColor = Color.white;
                    _style.wordWrap = true;
                }
                return _style;
            }
        }
        #endregion

        #region Files
        private class FileData
        {
            public bool selected = false;
            public FileResponse fileResponse;
            public FileData(FileResponse fileResponse)
            {
                this.fileResponse = fileResponse;
            }
        }
        private class VectorStoreData
        {
            public bool isDirty = false;
            public string Name;
            public List<string> fileIds = new List<string>();
            public HashSet<string> addFileIds = new HashSet<string>();
            public HashSet<string> removeFileIds = new HashSet<string>();
            public VectorStoreResponse response;
            public VectorStoreData(VectorStoreResponse vectorStoreResponse)
            {
                this.response = vectorStoreResponse;
                Name = response.Name;
            }
        }

        private FilePurpose filePurpose = FilePurpose.Assistants;
        private string createVectorStoreName;
        private List<string> filePaths = new List<string>();
        private List<FileData> files = new List<FileData>();
        private List<VectorStoreData> vectorStores = new List<VectorStoreData>();
        private Vector2 vectorStoreScrollPosition;
        private float vectorStoreViewHeight = 150;
        private Vector2 vectorStoreListScrollPosition;
        private Vector2 fileListScrollPosition;
        private float fileListViewHeight = 150;
        private bool isDraggingFileList = false;
        private bool isDraggingVectorStoreView = false;
        private VectorStoreData selectedVectorStore;
        private Vector2 uploadFileListScrollPosition;

        private void DrawFileDropArea()
        {
            EditorGUILayout.BeginHorizontal();
            {
                EditorGUILayout.BeginVertical(GUILayout.Width(200), GUILayout.Height(100));
                {
                    // Draw a drag drop area to upload files
                    GUILayout.Label("Drag and drop files here to upload", EditorStyles.helpBox, GUILayout.ExpandHeight(true));
                    var dragArea = GUILayoutUtility.GetLastRect();
                    if (dragArea.Contains(Event.current.mousePosition))
                    {
                        if (Event.current.type == EventType.DragUpdated)
                        {
                            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                            Event.current.Use();
                        }
                        else if (Event.current.type == EventType.DragPerform)
                        {
                            DragAndDrop.AcceptDrag();
                            // store file paths
                            foreach (var path in DragAndDrop.paths)
                            {
                                filePaths.Add(path);
                            }
                            Event.current.Use();
                        }
                    }
                    if (GUILayout.Button("Clear"))
                    {
                        filePaths.Clear();
                    }

                    // Tool buttons
                    EditorGUILayout.BeginHorizontal(GUILayout.Width(200));
                    {
                        // Select Purpose
                        filePurpose = (FilePurpose)EditorGUILayout.EnumPopup(filePurpose);
                        if (GUILayout.Button("Upload"))
                        {
                            UploadFiles(purpose: filePurpose, cancellationTokenSource.Token).Forget();
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndVertical();

                uploadFileListScrollPosition = EditorGUILayout.BeginScrollView(uploadFileListScrollPosition, GUILayout.Height(60));
                {
                    foreach (var path in filePaths)
                    {
                        EditorGUILayout.LabelField(path);
                    }
                }
                EditorGUILayout.EndScrollView();
            }
            EditorGUILayout.EndHorizontal();
        }
        /// The intended purpose of the uploaded file.
        /// Use 'assistants' for Assistants and Message files,
        /// 'vision' for Assistants image file inputs,
        /// 'batch' for Batch API,
        /// and 'fine-tune' for Fine-tuning.
        enum FilePurpose
        {
            Assistants,
            Vision,
            Batch,
            FineTune
        }
        private string GetPurpose(FilePurpose filePurpose)
        {
            switch (filePurpose)
            {
                default:
                case FilePurpose.Assistants:
                    return "assistants";
                case FilePurpose.Vision:
                    return "vision";
                case FilePurpose.Batch:
                    return "batch";
                case FilePurpose.FineTune:
                    return "fine-tune";
            }
        }
        private async Task UploadFiles(FilePurpose purpose, CancellationToken cancellationToken)
        {
            if (client == null || client.HasValidAuthentication == false)
            {
                LogError("Invalid client");
                return;
            }
            foreach (var path in filePaths)
            {
                try
                {
                    var response = await client.FilesEndpoint.UploadFileAsync(path, purpose: GetPurpose(purpose), cancellationToken:cancellationToken);
                    files.Add(new FileData(response));
                    // Log
                    Log($"Uploaded file: {response.Id}");
                }
                catch (Exception e)
                {
                    LogException(e);
                }
            }
        }

        private string GetFileName(string fileId)
        {
            var file = files.Find(f => f.fileResponse.Id == fileId);
            if (file != null)
            {
                return file.fileResponse.FileName;
            }
            return fileId;
        }

        private List<FileResponse> GetSelectFiles()
        {
            return this.files.Where(file => file.selected).Select(file => file.fileResponse).ToList();
        }

        private void OnDrawFiles()
        {
            DrawFileDropArea();

            var backupBGColor = GUI.backgroundColor;

            // File list
            fileListScrollPosition = EditorGUILayout.BeginScrollView(fileListScrollPosition, GUILayout.Height(fileListViewHeight));
            {
                if (GUILayout.Button("List Files"))
                {
                    ListFiles(cancellationTokenSource.Token).Forget();
                }
                // file response list
                foreach (var file in files)
                {
                    EditorGUILayout.BeginHorizontal();
                    {
                        file.selected = EditorGUILayout.Toggle(file.selected, GUILayout.Width(20));
                        if (GUILayout.Button(file.fileResponse.Id, GUILayout.Width(200)))
                        {
                            // clipboard
                            EditorGUIUtility.systemCopyBuffer = file.fileResponse.Id;
                        }
                        EditorGUILayout.LabelField(file.fileResponse.FileName, GUILayout.Width(200));
                        EditorGUILayout.LabelField(file.fileResponse.Purpose, GUILayout.Width(100));
                        // Size need thousand separator without floating point
                        EditorGUILayout.LabelField($"{file.fileResponse.Size:N0}", GUILayout.Width(100));
                        if (GUILayout.Button("Delete", GUILayout.Width(100)))
                        {
                            DeleteFile(file.fileResponse.Id, cancellationTokenSource.Token).Forget();
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.EndScrollView();

            DrawVerticalResizer(false, ref fileListViewHeight, ref isDraggingFileList);

            // Vector store list
            vectorStoreListScrollPosition = EditorGUILayout.BeginScrollView(vectorStoreListScrollPosition, GUILayout.ExpandHeight(true));
            {
                if (GUILayout.Button("List Vector Stores"))
                {
                    ListVectorStores(cancellationTokenSource.Token).Forget();
                }
                // vector store response list
                foreach (var vectorStore in vectorStores)
                {
                    EditorGUILayout.BeginHorizontal();
                    {
                        if (GUILayout.Button(vectorStore.response.Id, GUILayout.Width(200)))
                        {
                            // clipboard
                            EditorGUIUtility.systemCopyBuffer = vectorStore.response.Id;
                        }

                        // Select
                        if (selectedVectorStore == vectorStore)
                        {
                            GUI.backgroundColor = Color.green;
                        }
                        if (GUILayout.Button("Select", GUILayout.Width(100)))
                        {
                            selectedVectorStore = vectorStore;
                        }
                        GUI.backgroundColor = backupBGColor;

                        EditorGUILayout.LabelField(vectorStore.response.Name, GUILayout.Width(200));
                        EditorGUILayout.LabelField($"Count = {vectorStore.response.FileCounts.Completed}", GUILayout.Width(200));
                        if (GUILayout.Button("Delete", GUILayout.Width(100)))
                        {
                            DeleteVectorStore(vectorStore.response.Id, cancellationTokenSource.Token).Forget();
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.EndScrollView();

            // Create new vector store
            EditorGUILayout.Separator();
            EditorGUILayout.LabelField("Create Vector Store with selected files", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            {
                createVectorStoreName = EditorGUILayout.TextField(createVectorStoreName, GUILayout.Width(200));
                if (GUILayout.Button("+", GUILayout.Width(50)))
                {
                    CreateVectorStore(createVectorStoreName, GetSelectFiles(), cancellationTokenSource.Token).Forget();
                }
            }
            EditorGUILayout.EndHorizontal();

            // Vectore store edit
            if (selectedVectorStore != null)
            {
                // Resizer
                DrawVerticalResizer(true, ref vectorStoreViewHeight, ref isDraggingVectorStoreView);

                var backupGUIBGColor = GUI.backgroundColor;
                var backupGUIEnabled = GUI.enabled;
                vectorStoreScrollPosition = EditorGUILayout.BeginScrollView(vectorStoreScrollPosition, GUILayout.Height(vectorStoreViewHeight));
                {
                    EditorGUI.BeginChangeCheck();
                    {
                        // Vector store property
                        selectedVectorStore.Name = EditorGUILayout.TextField(selectedVectorStore.Name);

                        // Button to get file ID list of the vector store
                        if (GUILayout.Button("List Files"))
                        {
                            ListVectorStoreFiles(selectedVectorStore, cancellationTokenSource.Token).Forget();
                        }
                        // File ID list
                        EditorGUILayout.BeginHorizontal();
                        {
                            // Files in response
                            foreach (var fileId in selectedVectorStore.fileIds)
                            {
                                //EditorGUILayout.BeginHorizontal();
                                {
                                    bool alreadyRemoved = selectedVectorStore.removeFileIds.Contains(fileId);
                                    if (alreadyRemoved)
                                    {
                                        GUI.backgroundColor = Color.red;
                                    }
                                    if (GUILayout.Button(GetFileName(fileId), GUILayout.Width(200)))
                                    {
                                        // clipboard
                                        EditorGUIUtility.systemCopyBuffer = fileId;
                                    }
                                    if (alreadyRemoved)
                                    {
                                        if (GUILayout.Button("+", GUILayout.Width(20)))
                                        {
                                            selectedVectorStore.removeFileIds.Remove(fileId);
                                        }
                                    }
                                    else
                                    {
                                        if (GUILayout.Button("x", GUILayout.Width(20)))
                                        {
                                            selectedVectorStore.removeFileIds.Add(fileId);
                                        }
                                    }
                                    GUI.backgroundColor = backupGUIBGColor;
                                    GUI.enabled = backupGUIEnabled;
                                }
                                //EditorGUILayout.EndHorizontal();
                                //EditorGUILayout.Separator();
                            }

                            // Files in add list
                            string removeId = null;
                            foreach (var fileId in selectedVectorStore.addFileIds)
                            {
                                //EditorGUILayout.BeginHorizontal();
                                {
                                    GUI.backgroundColor = Color.green;
                                    if (GUILayout.Button(GetFileName(fileId), GUILayout.Width(200)))
                                    {
                                        // clipboard
                                        EditorGUIUtility.systemCopyBuffer = fileId;
                                    }
                                    if (GUILayout.Button("x", GUILayout.Width(20)))
                                    {
                                        removeId = fileId;
                                    }
                                    GUI.backgroundColor = backupGUIBGColor;
                                }
                                //EditorGUILayout.EndHorizontal();
                                //EditorGUILayout.Separator();
                            }
                            if (removeId != null)
                            {
                                selectedVectorStore.addFileIds.Remove(removeId);
                            }

                        }
                        EditorGUILayout.EndHorizontal();

                        // Add file ID list
                        EditorGUILayout.BeginHorizontal();
                        {
                            addVectorStoreID = EditorGUILayout.TextField(addVectorStoreID, GUILayout.Width(200));
                            if (GUILayout.Button("+", GUILayout.Width(50)))
                            {
                                selectedVectorStore.addFileIds.Add(addVectorStoreID);
                            }
                            if (GUILayout.Button("Add selected", GUILayout.Width(150)))
                            {
                                GetSelectFiles().ForEach(file => selectedVectorStore.addFileIds.Add(file.Id));
                            }
                        }
                        EditorGUILayout.EndHorizontal();

                    }
                    if (EditorGUI.EndChangeCheck())
                    {
                        selectedVectorStore.isDirty = true;
                    }
                }
                EditorGUILayout.EndScrollView();
                if (selectedVectorStore.isDirty)
                {
                    if (GUILayout.Button("Update"))
                    {
                        UpdateVectorStore(selectedVectorStore, cancellationTokenSource.Token).Forget();
                    }
                }

            }
        }

        private async Task ListVectorStoreFiles(VectorStoreData vectorStore, CancellationToken cancellationToken)
        {
            if (client == null || client.HasValidAuthentication == false)
            {
                LogError("Invalid client");
                return;
            }
            ListResponse<VectorStoreFileResponse> response = null;
            string after = null;
            vectorStore.fileIds.Clear();
            do
            {
                try
                {
                    ListQuery listQuery = new ListQuery(limit: 20, order: SortOrder.Descending, after: after);
                    response = await client.VectorStoresEndpoint.ListVectorStoreFilesAsync(vectorStore.response.Id, listQuery, cancellationToken: cancellationToken);
                    foreach (var file in response.Items)
                    {
                        vectorStore.fileIds.Add(file.Id);
                    }
                    if (response.Items.Count > 0)
                    {
                        after = response.LastId;
                    }
                }
                catch (Exception e)
                {
                    LogException(e);
                    break;
                }
            } while (response.HasMore);
        }

        private async Task DeleteFile(string fileId, CancellationToken cancellationToken)
        {
            if (client == null || client.HasValidAuthentication == false)
            {
                LogError("Invalid client");
                return;
            }
            try
            {
                await client.FilesEndpoint.DeleteFileAsync(fileId, cancellationToken);
                files.RemoveAll(file => file.fileResponse.Id == fileId);
            }
            catch (Exception e)
            {
                LogException(e);
            }
        }
        private async Task ListFiles(CancellationToken cancellationToken)
        {
            if (client == null || client.HasValidAuthentication == false)
            {
                LogError("Invalid client");
                return;
            }
            IReadOnlyList<FileResponse> response = null;
            try
            {
                response = await client.FilesEndpoint.ListFilesAsync(purpose: null, cancellationToken: cancellationToken);
                files.Clear();
                foreach (var file in response)
                {
                    files.Add(new FileData(file));
                }
            }
            catch (Exception e)
            {
                LogException(e);
            }
        }
        private async Task DeleteVectorStore(string vectorStoreId, CancellationToken cancellationToken)
        {
            if (client == null || client.HasValidAuthentication == false)
            {
                LogError("Invalid client");
                return;
            }
            try
            {
                await client.VectorStoresEndpoint.DeleteVectorStoreAsync(vectorStoreId, cancellationToken);
                vectorStores.RemoveAll(vectorStore => vectorStore.response.Id == vectorStoreId);
            }
            catch (Exception e)
            {
                LogException(e);
            }
        }
        private async Task ListVectorStores(CancellationToken cancellationToken)
        {
            if (client == null || client.HasValidAuthentication == false)
            {
                LogError("Invalid client");
                return;
            }
            string after = null;
            ListResponse<VectorStoreResponse> response = null;
            vectorStores.Clear();
            do
            {
                try
                {
                    ListQuery listQuery = new ListQuery(limit: 100, order: SortOrder.Descending, after: after);
                    response = await client.VectorStoresEndpoint.ListVectorStoresAsync(listQuery, cancellationToken);
                    foreach (var vectorStore in response.Items)
                    {
                        vectorStores.Add(new VectorStoreData(vectorStore));
                    }
                }
                catch (Exception e)
                {
                    LogException(e);
                    break;
                }
            } while (response.HasMore);
        }
        private async Task CreateVectorStore(string name, List<FileResponse> files, CancellationToken cancellationToken)
        {
            if (client == null || client.HasValidAuthentication == false)
            {
                LogError("Invalid client");
                return;
            }
            try
            {
                CreateVectorStoreRequest request = new CreateVectorStoreRequest(
                    name: name,
                    files: files,
                    expiresAfter: null, chunkingStrategy: null, metadata: null);
                var response = await client.VectorStoresEndpoint.CreateVectorStoreAsync(request, cancellationToken);
                vectorStores.Add(new VectorStoreData(response));
            }
            catch (Exception e)
            {
                LogException(e);
            }
        }
        private async Task UpdateVectorStore(VectorStoreData vectorStore, CancellationToken cancellationToken)
        {
            if (client == null || client.HasValidAuthentication == false)
            {
                LogError("Invalid client");
                return;
            }
            // Check modification of vector store
            if (vectorStore.Name != vectorStore.response.Name)
            {
                try
                {
                    var response = await client.VectorStoresEndpoint.ModifyVectorStoreAsync(vectorStore.response.Id, name:vectorStore.Name, cancellationToken: cancellationToken);
                    vectorStore.response = response;
                }
                catch (Exception e)
                {
                    LogException(e);
                }
            }
            // Check add files
            if (vectorStore.addFileIds.Count > 0)
            {
                VectorStoreFileBatchResponse batchResponse = null;
                try
                {
                    IReadOnlyList<string> addFileIds = vectorStore.addFileIds.ToList();
                    batchResponse = await client.VectorStoresEndpoint.CreateVectorStoreFileBatchAsync(vectorStore.response.Id, addFileIds, cancellationToken: cancellationToken);
                    vectorStore.addFileIds.Clear();
                }
                catch (Exception e)
                {
                    LogException(e);
                }

                while (batchResponse != null && batchResponse.Status != VectorStoreFileStatus.Completed)
                {
                    if (batchResponse.Status == VectorStoreFileStatus.Failed)
                    {
                        LogError("Failed to add files to the vector store");
                        break;
                    }
                    if (batchResponse.Status == VectorStoreFileStatus.Cancelled)
                    {
                        LogError("Cancelled to add files to the vector store");
                        break;
                    }
                    try
                    {
                        batchResponse = await client.VectorStoresEndpoint.GetVectorStoreFileBatchAsync(vectorStore.response.Id, batchResponse.Id, cancellationToken: cancellationToken);
                    }
                    catch (Exception e)
                    {
                        LogException(e);
                        break;
                    }
                }
            }
            // Check remove files
            if (vectorStore.removeFileIds.Count > 0)
            {
                IReadOnlyList<string> removeFileIds = vectorStore.removeFileIds.ToList();
                foreach (var fileId in removeFileIds)
                {
                    try
                    {
                        var result = await client.VectorStoresEndpoint.DeleteVectorStoreFileAsync(vectorStore.response.Id, fileId, cancellationToken: cancellationToken);
                        if (result)
                        {
                            vectorStore.removeFileIds.Remove(fileId);
                            this.Repaint();
                        }
                    }
                    catch (Exception e)
                    {
                        LogException(e);
                    }
                }
            }
        }
        #endregion

        #region Threads and Messages

        public class ThreadData
        {
            public string Id;
            public ThreadResponse response;
        }

        private const string threadsFileName = "thread.txt";
        private float threadViewWidth = 200;
        private bool isDraggingBarThread = false;
        private string threadId;
        private string apiKey;
        private string assistantId;
        private MessageRole msgRole = MessageRole.User;
        private string textPrompt;
        private string prefix;
        private string postfix;
        private Vector2 sideBarScrollPosition;
        private Vector2 messageScrollPosition;
        private AssistantResponse currentAssistant = null;
        private ThreadResponse currentThread = null;
        private List<ThreadData> threads = new List<ThreadData>();
        private List<MessageResponse> messages = new List<MessageResponse>();
        private RunResponse currentRun = null;
        private RunResponse currentThreadRun = null;

        private void DrawThreadSidebar()
        {
            var backupBGColor = GUI.backgroundColor;
            EditorGUILayout.BeginVertical(GUILayout.Width(threadViewWidth));
            {
                if (GUILayout.Button("Create Thread", GUILayout.Width(threadViewWidth)))
                {
                    CreateThread(doMessage: false, doRun: false, cancellationTokenSource.Token).Forget();
                }
                // Show run status
                if (currentThreadRun != null)
                {
                    EditorGUILayout.LabelField($"Run: {currentThreadRun.Status}, Assistant: {currentThreadRun.AssistantId}", EditorStyles.helpBox);
                }
                EditorGUILayout.BeginHorizontal(GUILayout.Width(threadViewWidth));
                {
                    threadId = EditorGUILayout.TextField("", threadId, GUILayout.Width(threadViewWidth-20));
                    if (GUILayout.Button("+", GUILayout.Width(20)))
                    {
                        threads.Add(new ThreadData() { Id = threadId });
                    }
                }
                EditorGUILayout.EndHorizontal();

                // Load threads from file
                if (GUILayout.Button("Load Threads", GUILayout.Width(threadViewWidth)))
                {
                    LoadThreadsFile();
                }

                sideBarScrollPosition = EditorGUILayout.BeginScrollView(sideBarScrollPosition, false, true);
                foreach (var threadData in threads)
                {
                    EditorGUILayout.BeginHorizontal();
                    {
                        GUI.backgroundColor = threadData.Id == threadId ? currentThread != null && currentThread.Id == threadId ? Color.green : Color.red : backupBGColor;
                        if (GUILayout.Button($"{threadData.Id}", GUILayout.Width(threadViewWidth - 40)))
                        {
                            threadId = threadData.Id;
                            RetrieveThread(cancellationTokenSource.Token).Forget();
                        }
                        if (GUILayout.Button("x", GUILayout.Width(20)))
                        {
                            DeleteThread(threadData.Id, cancellationTokenSource.Token).Forget();
                        }
                        GUI.backgroundColor = backupBGColor;
                    }
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndScrollView();
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawMessageList()
        {
            EditorGUILayout.BeginVertical();
            {
                // Send
                msgRole = (MessageRole)EditorGUILayout.EnumPopup("Role", msgRole);
                prefix = EditorGUILayout.TextField("Prefix", prefix);
                textPrompt = EditorGUILayout.TextArea(textPrompt);
                postfix = EditorGUILayout.TextField("Postfix", postfix);
                EditorGUILayout.BeginHorizontal();
                {
                    if (GUILayout.Button("Send", GUILayout.Width(100)))
                    {
                        SendMessage(doRun: true, cancellationTokenSource.Token).Forget();
                    }
                    if (GUILayout.Button("Send w/o Run", GUILayout.Width(100)))
                    {
                        SendMessage(doRun: false, cancellationTokenSource.Token).Forget();
                    }
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Separator();

                if (GUILayout.Button("Cancel all runs"))
                {
                    CancelRuns(cancellationTokenSource.Token).Forget();
                }
                // First, show run status
                if (currentRun != null)
                {
                    EditorGUILayout.LabelField($"Run: {currentRun.Status}, Assistant: {currentRun.AssistantId}", EditorStyles.helpBox);
                }

                messageScrollPosition = EditorGUILayout.BeginScrollView(messageScrollPosition);
                foreach (var message in messages)
                {
                    EditorGUILayout.BeginHorizontal();
                    {
                        EditorGUILayout.LabelField($"{message.Role}", EditorStyles.boldLabel);
                        if (!string.IsNullOrEmpty(message.AssistantId))
                        {
                            EditorGUILayout.TextField($"{message.AssistantId}");
                        }
                        if (!string.IsNullOrEmpty(message.RunId))
                        {
                            EditorGUILayout.TextField($"{message.RunId}");
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                    foreach (var content in message.Content)
                    {
                        // Multi-line label (support mulit-line and auto-wrap)
                        EditorGUILayout.TextArea(content.ToString(), style);
                    }
                    EditorGUILayout.Separator();
                }
                EditorGUILayout.EndScrollView();
            }
            EditorGUILayout.EndVertical();
        }

        private void OnDrawThreadsAndMessages()
        {
            var backupGUIEnabled = GUI.enabled;

            // Assistant
            EditorGUILayout.BeginHorizontal();
            {
                assistantId = EditorGUILayout.TextField("Assistant ID", assistantId);
                // Check current assistant. if not exists, draw a button to retrieve assistant.
                // If assistant exists, draw a check mark.
                if (currentAssistant == null)
                {
                    if (GUILayout.Button("Retrieve Assistant", GUILayout.Width(200)))
                    {
                        RetrieveCurrentAssistant();
                    }
                }
                else
                {
                    EditorGUILayout.LabelField("✔", GUILayout.Width(20));
                }

            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            {
                // Side bar - Thread list
                DrawThreadSidebar();

                DrawHorizontalResizer(false, ref threadViewWidth, ref isDraggingBarThread);

                // Message list
                DrawMessageList();
            }
            EditorGUILayout.EndHorizontal();
        }
        private async Task RetrieveThread(CancellationToken cancellationToken)
        {
            if (client == null || client.HasValidAuthentication == false)
            {
                LogError("Invalid client");
                return;
            }
            if (string.IsNullOrEmpty(threadId))
            {
                LogError("Thread ID is required");
                return;
            }
            try
            {
                var response = await client.ThreadsEndpoint.RetrieveThreadAsync(threadId);
                currentThread = response;

                await GetMessageList(cancellationToken);
            }
            catch (Exception e)
            {
                LogException(e);
            }
            this.Repaint();
        }

        private async Task DeleteThread(string threadId, CancellationToken cancellationToken)
        {
            if (client == null || client.HasValidAuthentication == false)
            {
                LogError("Invalid client");
                return;
            }
            if (string.IsNullOrEmpty(threadId))
            {
                LogError("Thread ID is required");
                return;
            }
            try
            {
                await client.ThreadsEndpoint.DeleteThreadAsync(threadId);
            }
            catch (Exception e)
            {
                LogException(e);
            }
            threads.RemoveAll(thread => thread.Id == threadId);
            SaveThreadsFile();
            this.Repaint();
        }

        private async Task CreateRun(Action<MessageResponse> onMsgCompleted, CancellationToken cancellationToken)
        {
            // Create Run
            var createRunRequest = new CreateRunRequest(assistantId: assistantId);
            try
            {
                MessageResponse newMsgRes = null;
                currentRun = await currentThread.CreateRunAsync(
                    createRunRequest,
                    streamEventHandler: async (streamEvent) =>
                    {
                        try
                        {
                            // Log event type
                            //Log($"Event type = <color=green>{streamEvent.GetType().Name}</color>");

                            switch (streamEvent)
                            {
                                case MessageResponse message:
                                    switch (message.Status)
                                    {
                                        case MessageStatus.InProgress:
                                            if (newMsgRes != message)
                                            {
                                                newMsgRes = message;
                                                messages.Insert(0, message);
                                            }
                                            else
                                            {
                                                messages[0] = message;
                                            }
                                            this.Repaint();
                                            break;
                                        case MessageStatus.Completed:
                                            messages[0] = message;
                                            onMsgCompleted?.Invoke(message);
                                            this.Repaint();
                                            break;
                                    }
                                    break;
                                case RunResponse run:
                                    break;
                                case Error error:
                                    throw error.Exception ?? new Exception(error.Message);
                            }
                        }
                        catch (Exception e)
                        {
                            LogException(e);
                        }
                        await Task.CompletedTask;
                    },
                    cancellationToken: cancellationToken
                );
            }
            catch (Exception e)
            {
                LogException(e);
            }

            // Custom polling
            while (currentRun.Status != RunStatus.Completed && cancellationToken.IsCancellationRequested == false)
            {
                Log($"Run ID = <color=yellow>{currentRun.Id}</color>, Status = <color=yellow>{currentRun.Status}</color>");
                if (currentRun.Status == RunStatus.Failed)
                {
                    LogError(currentRun.LastError);
                    break;
                }
                else if (currentRun.Status == RunStatus.RequiresAction)
                {
                    LogError("Unhandled RequiresAction");
                    break;
                }
                else if (currentRun.Status == RunStatus.Cancelled || currentRun.Status == RunStatus.Cancelling)
                {
                    LogError("Run is cancelled");
                    break;
                }
                else if (currentRun.Status == RunStatus.Expired)
                {
                    LogError("Run is expired");
                    break;
                }
                else if (currentRun.Status == RunStatus.Incomplete)
                {
                    LogError("Run is incomplete");
                    break;
                }

                // Refresh editor screen
                this.Repaint();
                await Task.Delay(1000);
                try
                {
                    currentRun = await currentThread.RetrieveRunAsync(currentRun.Id, cancellationToken);
                }
                catch (SystemException e)
                {
                    LogException(e);
                    break;
                }
                //Log($"Current run assistant = <color=yellow>{currentRun.AssistantId}</color>");
            }
            Log($"Run ID = <color=yellow>{currentRun.Id}</color>, Status = <color=yellow>{currentRun.Status}</color>");

            currentRun = null;
        }

        private async Task ListMessage(string before, CancellationToken cancellationToken)
        {
            // Get message (only latest than response)
            ListResponse<MessageResponse> latestMessages = null;
            do
            {
                try
                {
                    var listQuery = new ListQuery(limit: 20, order: SortOrder.Descending, before: before);
                    latestMessages = await currentThread.ListMessagesAsync(listQuery, cancellationToken);
                    foreach (var message in latestMessages.Items)
                    {
                        messages.Insert(0, message);
                    }
                    if (latestMessages.Items.Count > 0)
                        before = latestMessages.Items.First().Id;
                    await Task.Delay(1000);
                }
                catch (Exception e)
                {
                    LogException(e);
                    break;
                }
                await Task.Delay(1000);
            } while (latestMessages.HasMore);

            this.Repaint();
        }

        private async Task SendMessage(bool doRun, CancellationToken cancellationToken)
        {
            if (client == null || client.HasValidAuthentication == false)
            {
                LogError("Invalid client");
                return;
            }
            if (string.IsNullOrEmpty(threadId))
            {
                LogError("Thread ID is required");
                return;
            }
            if (currentThread == null)
            {
                LogError("No thread selected");
                return;
            }
            if (string.IsNullOrEmpty(textPrompt))
            {
                LogError("Text Prompt is required");
                return;
            }

            // Create message
            MessageResponse response = null;
            try
            {
                response = await currentThread.CreateMessageAsync(
                    new Message($"{prefix}{textPrompt}{postfix}", (OpenAI.Role)msgRole), cancellationToken: cancellationToken);
                messages.Insert(0, response);
                this.Repaint();
            }
            catch (Exception e)
            {
                LogException(e);
            }

            if (doRun == false)
            {
                return;
            }
            // Set before to the latest message
            var before = response.Id;

            await CreateRun((msg) => { before = msg.Id; }, cancellationToken);
            await ListMessage(before, cancellationToken);
        }
        private async Task GetMessageList(CancellationToken cancellationToken)
        {
            if (client == null || client.HasValidAuthentication == false)
            {
                LogError("Invalid client");
                return;
            }
            if (currentThread == null)
            {
                LogError("No thread selected");
                return;
            }
            try
            {
                messages.Clear();
                this.Repaint();
                string after = null;
                ListResponse<MessageResponse> response = null;
                do
                {
                    ListQuery listQuery = new ListQuery(limit: 20, order: SortOrder.Descending, after: after);
                    response = await currentThread.ListMessagesAsync(listQuery);
                    this.Repaint();
                    foreach (var messageResponse in response.Items)
                    {
                        messages.Add(messageResponse);
                    }
                    if (response.Items != null && response.Items.Count > 0)
                        after = response.Items.Last().Id;
                } while (response.HasMore);
            }
            catch (Exception e)
            {
                LogException(e);
            }
        }
        private async Task CreateThread(bool doMessage, bool doRun, CancellationToken cancellationToken)
        {
            if (client == null || client.HasValidAuthentication == false)
            {
                LogError("Invalid client");
                return;
            }
            if (currentAssistant == null)
            {
                currentAssistant = await RetrieveAssistantAsync(assistantId, cancellationToken);
            }

            if (currentAssistant == null)
            {
                LogError("No assistant selected");
                return;
            }
            var request = doMessage ? new CreateThreadRequest(textPrompt) : new CreateThreadRequest();
            ThreadResponse threadResponse = null;
            if (doRun)
            {
                try
                {
                    currentThreadRun = await currentAssistant.CreateThreadAndRunAsync(request, cancellationToken: cancellationToken);
                }
                catch (Exception e)
                {
                    LogException(e);
                }

                // Run
                while (currentThreadRun.Status != RunStatus.Completed)
                {
                    this.Repaint();
                    if (currentThreadRun.Status == RunStatus.Failed)
                    {
                        LogError(currentThreadRun.LastError);
                        break;
                    }
                    await Task.Delay(1000);
                    currentThreadRun = await currentThreadRun.UpdateAsync();
                    //Log($"Current run assistant = <color=yellow>{currentThreadRun.AssistantId}</color>");
                }
                Log($"Current assistant = <color=orange>{currentThreadRun.AssistantId}</color>");

                // Get Run
                threadResponse = await currentThreadRun.GetThreadAsync();
                currentThreadRun = null;
            }
            else
            {
                try
                {
                    threadResponse = await client.ThreadsEndpoint.CreateThreadAsync(request, cancellationToken: cancellationToken);
                    Log($"Thread ID = <color=yellow>{threadResponse.Id}</color>");
                }
                catch (Exception e)
                {
                    LogException(e);
                }
            }

            if (threadResponse != null)
            {
                LoadThreadsFile();
                threads.Add(new ThreadData() { Id = threadResponse.Id, response = threadResponse });
                SaveThreadsFile();
                this.Repaint();
            }
        }

        private void LoadThreadsFile()
        {
            // Load threads from file
            if (!System.IO.File.Exists(threadsFileName))
            {
                System.IO.File.Create(threadsFileName);
            }
            else
            {
                string[] lines;
                using (var reader = new StreamReader(threadsFileName))
                {
                    lines = reader.ReadToEnd().Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                }
                threads = lines.Select(line => new ThreadData() { Id = line }).ToList();
            }
        }

        private void SaveThreadsFile()
        {
            // Write threads to file
            var lines = threads.Select(thread => thread.Id).ToArray();
            using (var writer = System.IO.File.CreateText(threadsFileName))
            {
                foreach (var line in lines)
                {
                    writer.WriteLine(line);
                }
            }
        }

        #endregion

        #region Models
        private List<string> modelList = new List<string>();
        
        private async Task GetModels(CancellationToken cancellationToken)
        {
            if (client == null || client.HasValidAuthentication == false)
            {
                LogError("Invalid client");
                return;
            }
            try
            {
                var response = await client.ModelsEndpoint.GetModelsAsync(cancellationToken);
                modelList.Clear();
                foreach (var model in response)
                {
                    modelList.Add(model.Id);
                }
            }
            catch (Exception e)
            {
                LogException(e);
            }
        }

        // Popup window to pick model
        private int selectedModelIndex = -1;
        private void OnDrawModelPopup()
        {
            EditorGUILayout.BeginHorizontal();
            {
                if (modelList.Count > 0)
                {
                    selectedModelIndex = EditorGUILayout.Popup("Model", selectedModelIndex, modelList.ToArray());
                }
                if (GUILayout.Button("List Models", GUILayout.Width(100)))
                {
                    GetModels(cancellationTokenSource.Token).Forget();
                }
            }
            EditorGUILayout.EndHorizontal();
        }
        #endregion

        #region Assistant

        public class AssistantData
        {
            public bool isDrity = false;
            public string Name;
            public string Id;
            public string Description;
            public string Instructions;
            public string Model;
            [Range(0, 1)] public double Temperature;
            [Range(0, 1)] public double TopP;   // Nucleus Sampling: 0.0 to 1.0. 0.0 means no nucleus sampling, 1.0 means only the top word is considered.
            public HashSet<string> toolHashSet = new HashSet<string>();
            public HashSet<string> toolResourcesHashSet = new HashSet<string>();
            public List<string> vectorStoreIds = new List<string>();
            public string vectorStoreId => vectorStoreIds?.FirstOrDefault();
            private AssistantResponse _response;
            public AssistantResponse response
            {
                get 
                {
                    return _response;
                }
                set
                {
                    _response = value;
                    Name = response.Name;
                    Id = response.Id;
                    Description = response.Description;
                    Instructions = response.Instructions;
                    Model = response.Model;
                    Temperature = response.Temperature;
                    TopP = response.TopP;
                    if (response.Tools != null)
                    {
                        foreach (var tool in response.Tools)
                        {
                            toolHashSet.Add(tool.Type);
                        }
                    }
                    if (response.ToolResources != null)
                    {
                        if (response.ToolResources.FileSearch != null && response.ToolResources.FileSearch.VectorStoreIds != null)
                        {
                            vectorStoreIds.Clear();
                            foreach (var id in response.ToolResources.FileSearch.VectorStoreIds)
                            {
                                vectorStoreIds.Add(id);
                            }
                            toolResourcesHashSet.Add("file_search");
                        }
                    }
                }
            }
            public AssistantData(AssistantResponse response)
            {
                this.response = response;
            }
            public void DrawTool(string type)
            {
                // Tool
                var toggle = toolHashSet.Contains(type);
                var newToggle = EditorGUILayout.Toggle($"Tool: {type}", toggle);
                if (toggle != newToggle)
                {
                    if (newToggle)
                    {
                        toolHashSet.Add(type);
                    }
                    else
                    {
                        toolHashSet.Remove(type);
                    }
                }
                // Tool Resources
                toggle = toolResourcesHashSet.Contains(type);
                newToggle = EditorGUILayout.Toggle($"ToolResource: {type}", toggle);
                if (toggle != newToggle)
                {
                    if (newToggle)
                    {
                        toolResourcesHashSet.Add(type);
                    }
                    else
                    {
                        toolResourcesHashSet.Remove(type);
                    }
                }
            }
        }
        private float assistantViewHeight = 150;
        private bool isDraggingBarAssistant = false;
        private Vector2 assistantScrollPosition;
        private Vector2 assistantDetailScrollPos;
        private List<AssistantData> assistants = new List<AssistantData>();
        private string createAssistantName;
        private string createAssistantId;
        private string createAssistantInstructions;
        private string createAssistantModel;
        private string addVectorStoreID;
        private async Task<AssistantResponse> RetrieveAssistantAsync(string assistantId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(assistantId))
            {
                LogError("Assistant ID is required");
                return null;
            }
            if (client == null || client.HasValidAuthentication == false)
            {
                LogError("Invalid client");
                return null;
            }
            try
            {
                var response = await client.AssistantsEndpoint.RetrieveAssistantAsync(assistantId);
                return response;
            }
            catch (Exception e)
            {
                LogException(e);
            }
            return null;
        }

        private void RetrieveCurrentAssistant()
        {
            RetrieveAssistantAsync(assistantId, cancellationTokenSource.Token).ContinueWith(
                task =>
                {
                    this.Repaint();
                    if (task.IsFaulted)
                    {
                        LogException(task.Exception);
                    }
                    else
                    {
                        currentAssistant = task.Result;
                    }
                }
            );
        }

        private void DrawVectorStoreIds(AssistantData assistant)
        {
            if (assistant.vectorStoreIds != null)
            {
                string removeId = null;
                foreach (var id in assistant.vectorStoreIds)
                {
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button(id, GUILayout.Width(200)))
                    {
                        // clipboard
                        EditorGUIUtility.systemCopyBuffer = id;
                    }
                    if (GUILayout.Button("x", GUILayout.Width(50)))
                    {
                        removeId = id;
                    }
                    EditorGUILayout.EndHorizontal();
                }
                if (removeId != null)
                {
                    assistant.vectorStoreIds.Remove(removeId);
                }
            }
            EditorGUILayout.BeginHorizontal();
            addVectorStoreID = EditorGUILayout.TextField("Add VectorStore ID", addVectorStoreID);
            if (GUILayout.Button("Add VectorStore ID", GUILayout.Width(100)))
            {
                assistant.vectorStoreIds.Add(addVectorStoreID);
            }
            EditorGUILayout.EndHorizontal();
        }
        private void OnDrawAssistants()
        {
            EditorGUILayout.BeginHorizontal();
            {
                EditorGUILayout.BeginVertical();
                {
                    createAssistantName = EditorGUILayout.TextField("Name", createAssistantName);
                    createAssistantInstructions = EditorGUILayout.TextField("Instructions", createAssistantInstructions);
                    OnDrawModelPopup();
                }
                EditorGUILayout.EndVertical();
                if (selectedModelIndex >= 0)
                {
                    if (GUILayout.Button("Create Assistant"))
                    {
                        createAssistantModel = modelList[selectedModelIndex];
                        // Create assistant
                        CreateAssistant(createAssistantName, createAssistantInstructions, createAssistantModel, cancellationToken: cancellationTokenSource.Token).Forget();
                    }
                }
            }
            EditorGUILayout.EndHorizontal();

            // Draw separator
            EditorGUILayout.Separator();

            assistantScrollPosition = EditorGUILayout.BeginScrollView(assistantScrollPosition);
            if (GUILayout.Button("List Assistants"))
            {
                ListAssistants(cancellationTokenSource.Token).Forget();
            }
            foreach (var assistant in assistants)
            {
                // Draw assistant data (Simple)
                EditorGUILayout.BeginHorizontal();
                {
                    var backupGUIBGColor = GUI.backgroundColor;
                    GUI.backgroundColor = assistantId == assistant.Id ? Color.green : backupGUIBGColor;
                    if (GUILayout.Button(assistant.Id, GUILayout.Width(300)))
                    {
                        assistantId = assistant.Id;
                    }
                    if (GUILayout.Button("Copy", GUILayout.Width(50)))
                    {
                        EditorGUIUtility.systemCopyBuffer = assistant.Id;
                    }
                    GUI.backgroundColor = backupGUIBGColor;
                    {
                        EditorGUILayout.TextField(assistant.Name, GUILayout.Width(100));
                        if (GUILayout.Button(assistant.Instructions, GUILayout.Width(400), GUILayout.Height(20)))
                        {
                            EditorGUIUtility.systemCopyBuffer = assistant.Instructions;
                        }
                        EditorGUILayout.TextField(assistant.Model, GUILayout.Width(100));
                    }
                    if (GUILayout.Button("Delete", GUILayout.Width(100)))
                    {
                        DeleteAssistant(assistant.Id, cancellationTokenSource.Token).Forget();
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
            
            DrawVerticalResizer(true, ref assistantViewHeight, ref isDraggingBarAssistant);
            {
                var assistant = assistants.Find(a => a.Id == assistantId);

                if (assistant != null)
                {
                    assistantDetailScrollPos = EditorGUILayout.BeginScrollView(assistantDetailScrollPos, GUILayout.Height(assistantViewHeight));
                    {
                        EditorGUILayout.BeginVertical();
                        {
                            EditorGUI.BeginChangeCheck();
                            {
                                assistant.Name = EditorGUILayout.TextField("Name", assistant.Name);
                                assistant.Model = EditorGUILayout.TextField("Model", assistant.Model);
                                assistant.Temperature = EditorGUILayout.Slider("Temperature", (float)assistant.Temperature, 0f, 1f);
                                assistant.TopP = EditorGUILayout.Slider("Top P", (float)assistant.TopP, 0f, 1f);

                                // Tools & ToolResources
                                assistant.DrawTool("file_search");
                                // List vector store ids
                                DrawVectorStoreIds(assistant);
                                assistant.DrawTool("code_interpreter");

                                EditorGUILayout.LabelField("Instructions:", EditorStyles.boldLabel);
                                assistant.Instructions = EditorGUILayout.TextArea(assistant.Instructions);
                            }
                            if (EditorGUI.EndChangeCheck())
                            {
                                assistant.isDrity = true;
                            }
                        }
                        EditorGUILayout.EndVertical();
                    }
                    EditorGUILayout.EndScrollView();
                    if (assistant.isDrity)
                    {
                        if (GUILayout.Button("Update"))
                        {
                            // Update assistant
                            UpdateAssistant(assistant);
                        }
                    }
                }
            }
        }

        private void UpdateAssistant(AssistantData assistant)
        {
            if (assistant.isDrity == false)
            {
                return;
            }
            UpdateAssistantTask(assistant, cancellationTokenSource.Token).Forget();
        }
        private async Task UpdateAssistantTask(AssistantData assistantData, CancellationToken cancellationToken)
        {
            if (client == null || client.HasValidAuthentication == false)
            {
                LogError("Invalid client");
                return;
            }
            bool isFileSearch = assistantData.toolHashSet.Contains("file_search");
            bool isCodeInterpreter = assistantData.toolHashSet.Contains("code_interpreter");
            List<Tool> tools = new List<Tool>();
            if (isFileSearch)
            {
                tools.Add(new Tool(new FileSearchOptions(maxNumberOfResults: null, rankingOptions: null)));
            }
            if (isCodeInterpreter)
            {
                tools.Add(new Tool(Tool.CodeInterpreter));
            }
            FileSearchResources fileSearchResources = null;
            CodeInterpreterResources codeInterpreterResources = null;
            if (isFileSearch)
            {
                fileSearchResources = new FileSearchResources(vectorStoreId: assistantData.vectorStoreId);
            }
            if (isCodeInterpreter)
            {
                //codeInterpreterResources = new CodeInterpreterResources(fileId: null)
            }
            ToolResources toolResoures = new ToolResources(codeInterpreterResources, fileSearchResources);
            var request = new CreateAssistantRequest(
                            name: assistantData.Name,
                            description: assistantData.Description,
                            instructions: assistantData.Instructions,
                            model: assistantData.Model,
                            temperature: assistantData.Temperature,
                            tools: tools,
                            toolResources: toolResoures
                            );

            try
            {
                var response = await client.AssistantsEndpoint.ModifyAssistantAsync(assistantData.Id, request, cancellationToken);
                assistantData.response = response;
                return;
            }
            catch (Exception e)
            {
                LogException(e);
            }
            return;
        }

        private async Task<AssistantResponse> CreateAssistant(string name, string instructions, string model, CancellationToken cancellationToken)
        {
            if (client == null || client.HasValidAuthentication == false)
            {
                LogError("Invalid client");
                return null;
            }
            var request = new CreateAssistantRequest(model:model, name: name, instructions: instructions);
            try
            {
                var response = await client.AssistantsEndpoint.CreateAssistantAsync(request);
                return response;
            }
            catch (Exception e)
            {
                LogException(e);
            }
            return null;
        }
        private async Task ListAssistants(CancellationToken cancellationToken)
        {
            if (client == null || client.HasValidAuthentication == false)
            {
                LogError("Invalid client");
                return;
            }
            try
            {
                assistants.Clear();
                ListResponse<AssistantResponse> response = null;
                string after = null;
                do
                {
                    var listQuery = new ListQuery(limit: 20, order: SortOrder.Descending, after: after);
                    response = await client.AssistantsEndpoint.ListAssistantsAsync(listQuery);
                    foreach (var assistant in response.Items)
                    {
                        assistants.Add(new AssistantData(assistant));
                    }
                    after = response.Items.Last().Id;
                    this.Repaint();
                } while (response.HasMore);
            }
            catch (Exception e)
            {
                LogException(e);
            }
        }

        private async Task DeleteAssistant(string targetAssistantId, CancellationToken cancellationToken)
        {
            if (client == null || client.HasValidAuthentication == false)
            {
                LogError("Invalid client");
                return;
            }
            // Retrieve assistant to check instruction
            var assistant = assistants.Find(a => a.Id == targetAssistantId);
            if (assistant != null && !string.IsNullOrEmpty(assistant.Instructions))
            {
                // Show dialog
                if (!EditorUtility.DisplayDialog("Delete Assistant", $"Are you sure to delete assistant?\nInstruction length is {assistant.Instructions.Length}", "Yes", "No"))
                {
                    return;
                }
            }
            try
            {
                await client.AssistantsEndpoint.DeleteAssistantAsync(targetAssistantId);
                assistants.RemoveAll(assistant => assistant.Id == targetAssistantId);
            }
            catch (Exception e)
            {
                LogException(e);
            }
        }
        #endregion

        #region Todo
        async Task StreamEventHandler(IServerSentEvent streamEvent)
        {
            switch (streamEvent)
            {
                case ThreadResponse threadResponse:
                    currentThread = threadResponse;
                    break;
                case RunResponse runResponse:
                    if (runResponse.Status == RunStatus.RequiresAction)
                    {
                        var toolOutputs = await currentAssistant.GetToolOutputsAsync(runResponse);

                        foreach (var toolOutput in toolOutputs)
                        {
                            Log($"Tool Output: {toolOutput}");
                        }

                        await runResponse.SubmitToolOutputsAsync(toolOutputs, StreamEventHandler);
                    }
                    break;
                default:
                    Log(streamEvent.ToJsonString());
                    break;
            }
        }
        #endregion

        #region Fine-Tune
        
        private class FineTuneData
        {
            public string Id => response.Id;
            public FineTuneJobResponse response;
            public FineTuneData(FineTuneJobResponse response)
            {
                this.response = response;
            }
        }
        private List<FineTuneDataSet> fineTuneDataSet = new List<FineTuneDataSet>();

        private void OnDrawFineTune()
        {
            // Create fine-tune jsonl file

            if (GUILayout.Button("Open Jsonl"))
            {
                var path = EditorUtility.OpenFilePanel("Open Jsonl", "", "jsonl");
                if (!string.IsNullOrEmpty(path))
                {
                    using (var reader = new StreamReader(path))
                    {
                        // Read each line as a json file (jsonl)
                        fineTuneDataSet.Clear();
                        while (!reader.EndOfStream)
                        {
                            var line = reader.ReadLine();
                            var dataset = JsonConvert.DeserializeObject<FineTuneDataSet>(line);
                            fineTuneDataSet.Add(dataset);
                        }
                    }
                }
            }

            // Fine-tune jsonl file editor (A list of fine-tune messages)
            
            if (fineTuneDataSet != null)
            {
                var index = 0;
                var removeIndex = -1;
                foreach (var fineTuneData in fineTuneDataSet)
                {
                    EditorGUILayout.LabelField($"Fine-Tune Data {index}");
                    DrawFineTuneData(fineTuneData);
                    if (GUILayout.Button("Remove Data Set"))
                    {
                        removeIndex = index;
                    }
                    index++;
                }
                if (removeIndex >= 0)
                {
                    fineTuneDataSet.RemoveAt(removeIndex);
                }
                // Add data set button
                if (GUILayout.Button("Add Data Set"))
                {
                    fineTuneDataSet.Add(new FineTuneDataSet());
                }
            }
            
            // Save Jsonl file
            if (GUILayout.Button("Save Jsonl"))
            {
                var path = EditorUtility.SaveFilePanel("Save Jsonl", "", "fine-tune", "jsonl");
                if (!string.IsNullOrEmpty(path) && fineTuneDataSet != null)
                {
                    using (var writer = new StreamWriter(path))
                    {
                        foreach (var dataset in fineTuneDataSet)
                        {
                            writer.WriteLine(JsonConvert.SerializeObject(dataset, Formatting.None));
                        }
                    }
                }
            }

            // Create fine-tune job
            if (GUILayout.Button("Create Fine-Tune"))
            {
                var request = new CreateFineTuneJobRequest(
                    model: "",
                    trainingFileId: "",
                    hyperParameters: null,
                    suffix: null,
                    validationFileId: null
                    );
                client.FineTuningEndpoint.CreateJobAsync(request).ContinueWith(
                    task =>
                    {
                        if (task.IsFaulted)
                        {
                            LogException(task.Exception);
                        }
                        else
                        {
                            var fineTuneJob = new FineTuneData(task.Result);
                        }
                    }
                );
            }

            // List fine-tune jobs
            EditorGUILayout.BeginHorizontal();
            {
                if (GUILayout.Button("List Fine-Tune"))
                {
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawFineTuneData(FineTuneDataSet fineTuneDataSet)
        {
            if (fineTuneDataSet.messages == null)
            {
                fineTuneDataSet.messages = new List<FineTuneMessage>();
            }

            EditorGUILayout.BeginHorizontal();
            {
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.BeginVertical();
                {
                    FineTuneMessage removeMessage = null;
                    if (fineTuneDataSet.messages != null)
                    {
                        foreach (var message in fineTuneDataSet.messages)
                        {
                            EditorGUILayout.BeginHorizontal();
                            {
                                if (!Enum.TryParse(message.role, ignoreCase: true, out Role role))
                                {
                                    // To lower case
                                    role = Role.System;
                                    message.role = role.ToString().ToLower();
                                }
                                var newRole = (Role)EditorGUILayout.EnumPopup(role, GUILayout.Width(100));
                                if (newRole != role)
                                {
                                    message.role = newRole.ToString().ToLower();
                                }
                                message.content = EditorGUILayout.TextArea(message.content);
                                if (message.weight != null)
                                {
                                    message.weight = EditorGUILayout.Slider((float)message.weight, 0f, 1f, GUILayout.Width(200));
                                }
                                else
                                {
                                    if (GUILayout.Button("Add Weight", GUILayout.Width(100)))
                                    {
                                        message.weight = 1f;
                                    }
                                }
                                if (GUILayout.Button("x", GUILayout.Width(20)))
                                {
                                    removeMessage = message;
                                }
                            }
                            EditorGUILayout.EndHorizontal();
                        }
                    }
                    if (removeMessage != null)
                    {
                        fineTuneDataSet.messages.Remove(removeMessage);
                    }
                }
                EditorGUILayout.EndVertical();
                if (GUILayout.Button("Add Message", GUILayout.Width(100)))
                {
                    fineTuneDataSet.messages.Add(new FineTuneMessage());
                }
                if (EditorGUI.EndChangeCheck())
                {
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Run
        private async Task CancelRuns(CancellationToken cancellationToken)
        {
            if (client == null || client.HasValidAuthentication == false)
            {
                LogError("Invalid client");
                return;
            }
            List<RunResponse> runs = new List<RunResponse>();
            try
            {
                ListResponse<RunResponse> response = null;
                string after = null;
                do
                {
                    var listQuery = new ListQuery(limit: 100, after: after);
                    response = await client.ThreadsEndpoint.ListRunsAsync(currentThread.Id, listQuery);
                    foreach (var run in response.Items)
                    {
                        runs.Add(run);
                    }
                    after = response.Items.Last().Id;

                } while (response.HasMore && !cancellationToken.IsCancellationRequested);
            }
            catch (Exception e)
            {
                LogException(e);
            }

            foreach (var run in runs)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                if (run.Status == RunStatus.Completed || run.Status == RunStatus.Failed || run.Status == RunStatus.Expired || run.Status == RunStatus.Cancelled || run.Status == RunStatus.Cancelling)
                {
                    continue;
                }
                try
                {
                    await run.CancelAsync();
                }
                catch (Exception e)
                {
                    LogException(e);
                }
            }
        }
        #endregion

        #region View Resizer

        private void DrawHorizontalResizer(bool inverse, ref float width, ref bool isDragging)
        {
            GUILayout.Box("|\n|\n|", new GUIStyle("button"), GUILayout.ExpandHeight(true), GUILayout.Width(16));
            // Read drag and drop event to modify width
            var dragArea = GUILayoutUtility.GetLastRect();
            bool isContains = dragArea.Contains(Event.current.mousePosition);
            if (isContains)
            {
                if (Event.current.type == EventType.MouseDown)
                {
                    Event.current.Use();
                    isDragging = true;
                }
                else if (Event.current.type == EventType.MouseUp)
                {
                    isDragging = false;
                }
            }
            else if (isDragging)
            {
                if (Event.current.type == EventType.MouseDrag)
                {
                    if (inverse)
                        width -= Event.current.delta.x;
                    else
                        width += Event.current.delta.x;

                    Event.current.Use();
                }
                else if (Event.current.type == EventType.MouseUp)
                {
                    isDragging = false;
                }
            }
            width = Mathf.Clamp(width, 1, EditorGUIUtility.currentViewWidth);
            // change cursor when drag
            if (isDragging || isContains)
                EditorGUIUtility.AddCursorRect(dragArea, MouseCursor.ResizeHorizontal);
        }
        private void DrawVerticalResizer(bool inverse, ref float height, ref bool isDragging)
        {
            GUILayout.Box("----", new GUIStyle("button"), GUILayout.ExpandWidth(true), GUILayout.Height(8));
            // Read drag and drop event to modify height
            var dragArea = GUILayoutUtility.GetLastRect();
            var isContains = dragArea.Contains(Event.current.mousePosition);
            if (isContains)
            {
                if (Event.current.type == EventType.MouseDown)
                {
                    Event.current.Use();
                    isDragging = true;
                }
                else if (Event.current.type == EventType.MouseUp)
                {
                    isDragging = false;
                }
            }
            else if (isDragging)
            {
                if (Event.current.type == EventType.MouseDrag)
                {
                    if (inverse)
                        height -= Event.current.delta.y;
                    else
                        height += Event.current.delta.y;

                    Event.current.Use();
                }
                else if (Event.current.type == EventType.MouseUp)
                {
                    isDragging = false;
                }
            }
            height = Mathf.Clamp(height, 1, Screen.height);
            // change cursor when drag
            if (isDragging || isContains)
                EditorGUIUtility.AddCursorRect(dragArea, MouseCursor.ResizeVertical);
        }
        #endregion

        #region Log
        private void Log(object message)
        {
            Debug.Log(message, this);
        }
        private void LogWarning(object message)
        {
            Debug.LogWarning(message, this);
        }
        private void LogException(Exception e)
        {
            RestException restException = e as RestException;
            if (restException != null)
            {
                Debug.LogError($"<color=red>REST</color> {restException.Response.Code} {restException.Response.Error}\n{restException.Response.Body}, this");
            }
            Debug.LogException(e, this);
        }
        private void LogError(object message)
        {
            Debug.LogError(message, this);
        }
        #endregion
    }


    #region Extensions
    public static class TaskExtensions
    {
        public static void Forget(this Task task)
        {
            task.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    Debug.LogException(t.Exception);
                }
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
    #endregion Extension
}