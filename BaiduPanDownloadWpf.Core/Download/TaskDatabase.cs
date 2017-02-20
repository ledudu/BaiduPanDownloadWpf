﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BaiduPanDownloadWpf.Core.Download.DownloadCore;
using BaiduPanDownloadWpf.Core.Download.DwonloadCore;
using BaiduPanDownloadWpf.Core.ResultData;
using Microsoft.Practices.ObjectBuilder2;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using BaiduPanDownloadWpf.Infrastructure;

namespace BaiduPanDownloadWpf.Core.Download
{
    public class TaskDatabase
    {
        private static readonly List<TaskDatabase> List=new List<TaskDatabase>();

        public static TaskDatabase GetDatabaseByUser(LocalDiskUser user)
        {
            if (List.Any(v => v.Name == user.Name))
            {
                return List.FirstOrDefault(v => v.Name == user.Name);
            }
            var db=new TaskDatabase(user);
            List.Add(db);
            return db;
        }
        private TaskList _info;

        public string Name => _user.Name;
        private readonly LocalDiskUser _user;
        public string TaskListFile => Path.Combine(Directory.GetCurrentDirectory(), "Users", Name, "TaskList.json");

        private TaskDatabase(LocalDiskUser user)
        {
            _user = user;
            Reload();
        }

        private void Reload()
        {
            if (!File.Exists(TaskListFile))
            {
                _info = new TaskList();
                Save();
                return;
            }
            _info = JsonConvert.DeserializeObject<TaskList>(File.ReadAllText(TaskListFile));
            foreach (var path in _info.Tasks.Select(v => v.DownloadPath))
            {
                if (File.Exists(path + ".downloading"))
                {
                    _info.DownloadingList.Add(DownloadingFileData.Load(path+".downloading"));
                }
            }
        }

        public void Add(NetDiskFile file, string path)
        {
            if (Contains(path))
                return;
            _info.Tasks.Add(new TaskInfo()
            {
                DownloadFileInfo = file,
                DownloadPath = path,
            });
            var data=new DownloadingFileData()
            {
                Info = null,
                DownloadPath = path,
                FileInfo = file
            };
            data.Save();
            _info.DownloadingList.Add(data);
            Save();
        }

        public bool Contains(string path)
        {
            return _info.Tasks.Any(v => v.DownloadPath == path);
        }

        public bool Contains(long id)
        {
            return _info.Tasks.Any(v => v.DownloadFileInfo.FileId == id);
        }

        public bool Contains(NetDiskFile file)
        {
            return Contains(file.FileId);
        }

        /// <summary>
        /// 根据ID获取文件文件路径
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public string GetFilePathById(long id)
        {
            if (!Contains(id))
                return string.Empty;
            foreach (var task in _info.Tasks)
            {
                if (task.DownloadFileInfo.FileId == id)
                {
                    return task.DownloadPath;
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// 根据下载路径获取文件ID
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public long GetFileIdByPath(string path)
        {
            if (!Contains(path))
            {
                return -1L;
            }
            foreach (var task in _info.Tasks)
            {
                if (task.DownloadPath == path)
                {
                    return task.DownloadFileInfo.FileId;
                }
            }
            return -1L;
        }

        public long GetFileIdByDownloadInfo(DownloadInfo info)
        {
            return GetFileIdByPath(info.DownloadPath);
        }

        /// <summary>
        /// 获取下一个任务的链接
        /// </summary>
        public async Task<NextResult> Next()
        {
            var info = _info.DownloadingList.FirstOrDefault(v => v.Info == null);
            if (info == null)
            {
                Console.WriteLine("DEBUG: 没有新任务了");
                return new NextResult(null,209,"没有下一个任务了");
            }
            var ret = await _user.DownloadFiles(new[] {info.FileInfo}, DownloadMethod.JumpDownload);
            if (ret.ErrorCode != 0)
            {
                ret = await _user.DownloadFiles(new[] {info.FileInfo}, DownloadMethod.AppidDownload);
                if (ret.ErrorCode != 0)
                {
                    Console.WriteLine("DEBUG: 获取下载链接失败");
                    return new NextResult(null, ret.ErrorCode, "获取下载链接失败");
                }
                else
                {
                    var result = await CreateData(info, ret);
                    return result;
                }
            }
            else
            {
                var result = await CreateData(info, ret);
                if (result == null)
                {
                    ret = await _user.DownloadFiles(new[] { info.FileInfo }, DownloadMethod.AppidDownload);
                    if (ret.ErrorCode != 0)
                    {
                        Console.WriteLine("DEBUG: 获取下载链接失败");
                        return new NextResult(null, ret.ErrorCode, "获取下载链接失败");
                    }
                    result = await CreateData(info, ret);
                }
                return result;
            }
        }

        public async Task<NextResult> CreateData(DownloadingFileData info,DownloadResult result)
        {
            try
            {
                foreach (var url in result.DownloadUrlList)
                {
                    var httpInfo = await HttpDownload.CreateTaskInfo(url.Value.UrlLists, info.DownloadPath, 32,
                        result.Cookies);
                    info.Info = httpInfo;
                    info.Save();
                    return new NextResult(httpInfo, 0, string.Empty);
                }
                return null;
            }
            catch (NullReferenceException)
            {
                return null;
            }
        }

        public TaskInfo[] GetUncompletedList()
        {
            return _info.Tasks.ToArray();
        }

        public CompletedTask[] GetCompletedList()
        {
            return _info.CompletedTasks.ToArray();
        }

        public DownloadingFileData[] GetDownloadingTask()
        {
            return _info.DownloadingList.Where(v=>v.Info!=null).ToArray();
        }

        public DownloadingFileData GetDownloadingDataByPath(string path)
        {
            return GetDownloadingTask().FirstOrDefault(v => v.DownloadPath == path);
        }

        /// <summary>
        /// 设置任务为完成状态
        /// </summary>
        /// <param name="path"></param>
        public void SetCompleted(string path)
        {
            if (!Contains(path))
                return;
            _info.CompletedTasks.Insert(0, new CompletedTask() { DownloadPath=path,Id=GetFileIdByPath(path),FileInfo=_info.Tasks.FirstOrDefault(v=>v.DownloadPath==path).DownloadFileInfo,CompletedTime=DateTime.Now});
            _info.Tasks.Remove(_info.Tasks.FirstOrDefault(v => v.DownloadPath == path));
            GetDownloadingDataByPath(path).DeleteFile();
            _info.DownloadingList.Remove(GetDownloadingDataByPath(path));
            Save();
        }

        /// <summary>
        /// 删除任务
        /// </summary>
        /// <param name="path"></param>
        public void RemoveTask(long id)
        {
            if (_info.Tasks.Any(v => v.Id == id))
            {
                var path = GetFilePathById(id);
                if (GetDownloadingDataByPath(path) != null)
                    SetCompleted(path);
                _info.Tasks.Remove(_info.Tasks.FirstOrDefault(v => v.DownloadPath == path));
            }
            if (_info.CompletedTasks.Any(v => v.Id == id))
            {
                _info.CompletedTasks.Remove(_info.CompletedTasks.FirstOrDefault(v=>v.Id==id));
            }
            Save();
        }

        /// <summary>
        /// 更新任务数据
        /// </summary>
        /// <param name="info"></param>
        public void UpdateTask(DownloadInfo info)
        {
            if(info==null) return;
            var data = GetDownloadingDataByPath(info.DownloadPath);
            data.Info = info;
            data.Save();
        }

        /// <summary>
        /// 保存任务信息
        /// </summary>
        public void Save()
        {
            File.WriteAllText(TaskListFile, JObject.Parse(JsonConvert.SerializeObject(_info)).ToString());
        }

    }

    public class TaskInfo
    {
        public NetDiskFile DownloadFileInfo { get; set; }

        public string DownloadPath { get; set; }

        [JsonIgnore]
        public long Id => DownloadFileInfo.FileId;
    }

    public class CompletedTask
    {
        public string DownloadPath { get; set; }
        public NetDiskFile FileInfo { get; set; }
        public long Id { get; set; }
        public DateTime CompletedTime { get; set; }
    }

    public class DownloadingFileData
    {
        public DownloadInfo Info { get; set; }
        public NetDiskFile FileInfo { get; set; }
        public string DownloadPath { get; set; }

        public void Save()
        {
            File.WriteAllText(DownloadPath + ".downloading", JObject.Parse(JsonConvert.SerializeObject(this)).ToString());
        }

        public void DeleteFile()
        {
            File.Delete(DownloadPath + ".downloading");
        }

        public static DownloadingFileData Load(string path)
        {
            return JsonConvert.DeserializeObject<DownloadingFileData>(File.ReadAllText(path));
        }
    }

    public class TaskList
    {
        public List<TaskInfo> Tasks { get; set; } = new List<TaskInfo>();

        public List<CompletedTask> CompletedTasks { get; set; } = new List<CompletedTask>();

        [JsonIgnore]
        public List<DownloadingFileData> DownloadingList { get; set; } = new List<DownloadingFileData>();
    }


    public class NextResult
    {
        public DownloadInfo Info { get; }
        public int ErrorCode { get; }
        public string ErrorMessage { get; }

        public NextResult(DownloadInfo info, int errorCode, string errorMessage)
        {
            Info = info;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
        }
    }
}
