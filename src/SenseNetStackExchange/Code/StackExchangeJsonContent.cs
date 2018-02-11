using System;
using System.Collections.Generic;
using System.Text;
using SenseNet.ContentRepository.Storage;
using SenseNet.ContentRepository.Schema;
using SenseNet.ContentRepository.Storage.Security;
using System.Net;
using System.Text.RegularExpressions;
using System.IO;
using System.Web;
using System.Web.Caching;
using SenseNet.Diagnostics;
using System.Configuration;
using SenseNet.ContentRepository;
using System.Threading;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace SenseNet.StackExchange.ContentHandler
{ 
    [ContentHandler]
    public class StackExchangeJsonContent : SenseNet.ContentRepository.File, IHttpHandler
    {
        public StackExchangeJsonContent(Node parent) : this(parent, null) { }
        public StackExchangeJsonContent(Node parent, string nodeTypeName) : base(parent, nodeTypeName) { }
        protected StackExchangeJsonContent(NodeToken nt) : base(nt) { }

        private static List<int> lockList = new List<int>();

        //****************************************** Start of Repository Properties *********************************************//

        private string BaseUrl
        {
            get
            {
                return Settings.GetValue<string>("StackExchangeApi", "api_uri", this.Path, string.Empty);
            }

        }

        static string ReplaceParam(Match m)
        {
            string x = m.ToString().Replace("@", "");
            var param = HttpContext.Current.Request.QueryString.Get(x);
            if (!string.IsNullOrWhiteSpace(param))
            {
                return param;
            }
            return "";
        }

        private string CustomUrl
        {
            get
            {
                Regex regx = new Regex(@"@@.+@@");
                string Pathed = regx.Replace(ApiPath, new MatchEvaluator(ReplaceParam));
                return $"{BaseUrl}{Pathed}";
            }
          
        }

        [RepositoryProperty("ApiPath", RepositoryDataType.String)]
        public string ApiPath
        {
            get { return this.GetProperty<string>("ApiPath"); }
            set { this["ApiPath"] = value; }
        }

        [RepositoryProperty("IsCacheable", RepositoryDataType.Int)]
        public virtual bool IsCacheable
        {
            get { return (this.HasProperty("IsCacheable")) ? (this.GetProperty<int>("IsCacheable") != 0) : false; }
            set { this["IsCacheable"] = value ? 1 : 0; }
        }

        [RepositoryProperty("IsPersistable", RepositoryDataType.Int)]
        public virtual bool IsPersistable
        {
            get { return (this.HasProperty("IsPersistable")) ? (this.GetProperty<int>("IsPersistable") != 0) : false; }
            set { this["IsPersistable"] = value ? 1 : 0; }
        }

        [RepositoryProperty("IsErrorRelevant", RepositoryDataType.Int)]
        public virtual bool IsErrorRelevant
        {
            get { return (this.HasProperty("IsErrorRelevant")) ? (this.GetProperty<int>("IsErrorRelevant") != 0) : false; }
            set { this["IsErrorRelevant"] = value ? 1 : 0; }
        }

        [RepositoryProperty("JsonUpdateInterval")]
        public int JsonUpdateInterval
        {
            get
            {
                int result = base.GetProperty<int>("JsonUpdateInterval");

                if (result < 1 && !string.IsNullOrWhiteSpace(this.CustomUrl))
                {
                    result = MinUpdateInterval ?? 1;
                }

                return result;
            }
            set { base.SetProperty("JsonUpdateInterval", value); }
        }

        [RepositoryProperty("JsonLastUpdate", RepositoryDataType.DateTime)]
        private DateTime JsonLastUpdate
        {
            get { return base.GetProperty<DateTime>("JsonLastUpdate"); }
            set { base.SetProperty("JsonLastUpdate", value); }
        }

        [RepositoryProperty("JsonLastSyncDate", RepositoryDataType.DateTime)]
        private DateTime JsonLastSyncDate
        {
            get { return base.GetProperty<DateTime>("JsonLastSyncDate"); }
            set { base.SetProperty("JsonLastSyncDate", value); }
        }


        [RepositoryProperty("Binary", RepositoryDataType.Binary)]
        public override BinaryData Binary
        {
            get
            {
                BinaryData result = null;

                if (this.IsCacheable)
                    result = GetBinaryFromCache();

                if (result == null)
                    result = this.GetBinary("Binary");

                return result;
            }
            set { this.SetBinary("Binary", value); }
        }

        [RepositoryProperty("ResponseEncoding", RepositoryDataType.String)]
        public virtual string ResponseEncoding
        {
            get { return this.GetProperty<string>("ResponseEncoding"); }
            set { this["ResponseEncoding"] = value; }
        }

        [RepositoryProperty("TechnicalUser", RepositoryDataType.Reference)]
        public User TechnicalUser
        {
            get { return base.GetReference<User>("TechnicalUser"); }
            set { base.SetReference("TechnicalUser", value); }
        }

        public const string CACHECONTROL = "CacheControl";
        [RepositoryProperty(CACHECONTROL, RepositoryDataType.String)]
        public string CacheControl
        {
            get { return (this.HasProperty(CACHECONTROL)) ? this.GetProperty<string>(CACHECONTROL) : string.Empty; }
            set { this[CACHECONTROL] = value; }
        }

        public const string MAXAGE = "MaxAge";
        [RepositoryProperty(MAXAGE, RepositoryDataType.String)]
        public virtual string MaxAge
        {
            get { return (this.HasProperty(MAXAGE)) ? this.GetProperty<string>(MAXAGE) : string.Empty; }
            set { this[MAXAGE] = value; }
        }

        //****************************************** Start of Properties *********************************************//

        private string CacheId
        {
            get { return "JsonContentCache_" + this.Id; }
        }

        private bool _errorOccured = false;
        private bool ErrorOccured
        {
            get { return _errorOccured; }
            set { _errorOccured = value; }
        }

        private bool IsExpired
        {
            get
            {
                return this.JsonLastSyncDate < DateTime.Now.ToUniversalTime().AddMinutes(-this.JsonUpdateInterval);
            }
        }

        public HttpCacheability CacheControlEnumValue
        {
            get
            {
                var strprop = this.CacheControl;
                if (string.IsNullOrEmpty(strprop) || strprop == "Nondefined")
                    return HttpCacheability.Public;

                return (HttpCacheability)Enum.Parse(typeof(HttpCacheability), strprop, true);
            }
        }

        public int NumericMaxAge
        {
            get
            {
                var strprop = this.MaxAge;
                if (!string.IsNullOrWhiteSpace(strprop))
                {
                    int val;
                    if (Int32.TryParse(strprop, out val))
                        return val;
                }
                return 0;
            }
        }

        private bool IsInCache
        {
            get
            {
                var result = HttpContext.Current.Cache.Get(this.CacheId);
                return result != null;
            }
        }

        private bool IsRefreshTime
        {
            get
            {
                bool result = false;

                result = (this.IsPersistable && this.IsExpired) || (this.IsCacheable && !this.IsInCache);

                if (string.IsNullOrWhiteSpace(this.CustomUrl))
                {
                    result = false;
                }

                return result;
            }
        }

        public string InnerText
        {
            get
            {
                return this.ToString();
            }
        }


        //****************************************** Start of Timeout *********************************************//
        private static object __timeoutSync = new object();
        private static object __intervalSync = new object();
        private static int? _timeout;
        public static int? Timeout
        {
            get
            {
                if (!_timeout.HasValue)
                {
                    lock (__timeoutSync)
                    {
                        if (!_timeout.HasValue)
                        {
                            var setting = "3000";
                            int value;
                            if (!string.IsNullOrEmpty(setting) && Int32.TryParse(setting, out value))
                                _timeout = value;
                        }
                    }
                }
                return _timeout;
            }
        }

        private static int? _minUpdateInterval;
        public static int? MinUpdateInterval
        {
            get
            {
                if (!_minUpdateInterval.HasValue)
                {
                    lock (__intervalSync)
                    {
                        if (!_minUpdateInterval.HasValue)
                        {
                            var setting = "60";
                            int value;
                            if (!string.IsNullOrEmpty(setting) && Int32.TryParse(setting, out value))
                                _minUpdateInterval = value;
                        }
                    }
                }
                return _minUpdateInterval;
            }
        }



        //****************************************** Start of Overrides *********************************************//

        protected override void OnLoaded(object sender, SenseNet.ContentRepository.Storage.Events.NodeEventArgs e)
        {
            base.OnLoaded(sender, e);

            if (!SenseNet.Configuration.RepositoryEnvironment.WorkingMode.Importing)
                DoRefreshLogic();

        }

        private void DoRefreshLogic()
        {
            if (this.IsRefreshTime)
            {
                if (ReclaimLock(this.Id))
                {
                    try
                    {
                        if (this.IsRefreshTime)
                        {
                            RefreshDocument();
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.WriteException(e);
                    }
                    finally
                    {
                        ReleaseLock(this.Id);
                    }
                }
            }
        }

        private bool ReclaimLock(int id)
        {
            bool result = false;
            if (!lockList.Contains(id))
            {
                lock (lockList)
                {
                    if (!lockList.Contains(id))
                    {
                        lockList.Add(id);
                        result = true;
                    }
                }
            }
            return result;
        }

        private void ReleaseLock(int id)
        {
            if (lockList.Contains(id))
            {
                lock (lockList)
                {
                    lockList.Remove(id);
                }
            }
        }

        public override object GetProperty(string name)
        {
            switch (name)
            {
                case "Binary":
                    return this.Binary;
                case "ResponseEncoding":
                    return this.ResponseEncoding;
                case CACHECONTROL:
                    return this.CacheControl;
                case MAXAGE:
                    return this.MaxAge;
                case "Stream":
                    return this.Binary.GetStream();
                case "TechnicalUser":
                    return this.TechnicalUser;
                case "IsCacheable":
                    return this.IsCacheable;
                case "IsPersistable":
                    return this.IsPersistable;
                case "IsErrorRelevant":
                    return this.IsErrorRelevant;
                case "InnerText":
                    return this.InnerText;
                default:
                    break;
            }

            return base.GetProperty(name);
        }

        public override void SetProperty(string name, object value)
        {
            switch (name)
            {
                case "ResponseEncoding":
                    this.ResponseEncoding = value.ToString();
                    break;
                case "TechnicalUser":
                    TechnicalUser = (User)value;
                    break;
                case CACHECONTROL:
                    this.CacheControl = (string)value;
                    break;
                case MAXAGE:
                    this.MaxAge = (string)value;
                    break;
                case "IsCacheable":
                    this.IsCacheable = (bool)value;
                    break;
                case "IsPersistable":
                    this.IsPersistable = (bool)value;
                    break;
                case "IsErrorRelevant":
                    this.IsErrorRelevant = (bool)value;
                    break;
                default:
                    base.SetProperty(name, value);
                    break;
            }
        }

        //****************************************** Start of Helpers *********************************************//
        private JToken GetDocumentFromCache()
        {
            JToken cachedDocument = null;
            object cachedObject = HttpContext.Current.Cache.Get(this.CacheId);
            if (cachedObject != null)
                cachedDocument = cachedObject as JToken;
            return cachedDocument;
        }

        private BinaryData GetBinaryFromCache()
        {
            BinaryData binData = new BinaryData();

            JToken cachedDocument = GetDocumentFromCache();
            if (cachedDocument != null)
            {
                string stringDocument = cachedDocument.ToString(); // TechDebt: ez vajh mukodik?
                byte[] byteArrayDocument = Encoding.UTF8.GetBytes(stringDocument);

                MemoryStream streamDocument = new MemoryStream(byteArrayDocument);
                if (streamDocument != null)
                {
                    binData.SetStream(streamDocument);
                }
            }
            return binData;
        }

        public bool RefreshDocument()
        {
            this.ErrorOccured = false;
            bool saveIt = false;

            JToken document = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(this.CustomUrl))
                {
                    saveIt = true;
                    Regex regex = new Regex("((.+)://)((.*)@)*(.*)");
                    HttpWebRequest feedRequest = (HttpWebRequest)WebRequest.Create(HttpUtility.HtmlDecode(CustomUrl));
                    feedRequest.AutomaticDecompression = DecompressionMethods.GZip;

                    feedRequest.Timeout = Timeout ?? 3000; 

                    string sb = string.Empty;
                    using (HttpWebResponse response = (HttpWebResponse)feedRequest.GetResponse())
                    {
                        using (Stream streamData = response.GetResponseStream())
                        {
                            using (StreamReader reader = new StreamReader(streamData))
                            {
                                string json = reader.ReadToEnd();
                                document = Newtonsoft.Json.Linq.JToken.Parse(json);
                                
                            }
                        }
                    }

                }
            }
            catch (Exception e)
            {
                string excMsg = e.Message;
                string inExcMsg = (e.InnerException != null) ? e.InnerException.Message : string.Empty;
                document = getErrorDocument(excMsg, inExcMsg);
                this.ErrorOccured = true;
            }

            if (saveIt && this.IsCacheable && (!this.ErrorOccured || this.IsErrorRelevant))
            {
                SetDocumentToCache(document);
            }

            if (saveIt && this.IsPersistable)
            {
                if (!this.ErrorOccured || this.IsErrorRelevant)
                { 
                    SetDocumentToBinary(document);
                }
                else
                {
                    SetLastSyncDate();
                }
            }

            return !this.ErrorOccured;
        }

        private void SetDocumentToCache(JToken document)
        {
            HttpContext.Current.Cache.Insert(this.CacheId, document, null, DateTime.Now.AddMinutes(this.JsonUpdateInterval), System.Web.Caching.Cache.NoSlidingExpiration, CacheItemPriority.Normal, null);
        }

        private void SetDocumentToBinary(JToken document)
        {
            string sb = document.ToString(); 
            byte[] byteArray = Encoding.UTF8.GetBytes(sb);
            BinaryData binData = new BinaryData();
            MemoryStream setStream = new MemoryStream(byteArray);
            if (setStream != null)
            {
                binData.SetStream(setStream);
                this.SetBinary("Binary", binData);
                this.Binary = binData;
            }

            DateTime now = DateTime.Now;
            this.JsonLastUpdate = now;
            this.JsonLastSyncDate = now;
            SaveAsTechnicalUser();
        }

        private void SetLastSyncDate()
        {
            DateTime now = DateTime.Now;
            this.JsonLastSyncDate = now;
            SaveAsTechnicalUser();
        }


        private void SaveAsTechnicalUser()
        {
            var oldUSer = User.Current;
            try
            {
                if (TechnicalUser != null)
                    User.Current = TechnicalUser;

                if (User.Current as Node == null)
                    User.Current = User.Administrator;

                using (new SystemAccount())
                {
                    var count = 3;
                    var ok = false;
                    Exception ex = null;
                    var nodeToSave = Node.Load<StackExchangeJsonContent>(this.Id);
                    while (!ok && count > 0)
                    {
                        try
                        {
                            if (nodeToSave == null || nodeToSave != this)
                            {
                                nodeToSave = Node.Load<StackExchangeJsonContent>(this.Id);
                                nodeToSave.SetBinary("Binary", this.Binary);
                                nodeToSave["JsonLastUpdate"] = this.JsonLastUpdate;
                                nodeToSave["JsonLastSyncDate"] = this.JsonLastSyncDate;
                                nodeToSave.ModificationDate = DateTime.Now;
                            }
                            nodeToSave.Save();
                            ok = true;
                        }
                        catch (NodeIsOutOfDateException e)
                        {
                            count--;
                            ex = e;
                            nodeToSave = null;
                            Thread.Sleep(1500);
                        }
                    }

                    if (!ok)
                    {
                        throw new ApplicationException("JsonContent - Frissítési hiba: ", ex);
                    }
                }
            }
            finally
            {
                User.Current = oldUSer;
            }
        }


        protected virtual Encoding GetEncoding()
        {
            Encoding encoding = Encoding.UTF8;
            if (string.IsNullOrEmpty(this.ResponseEncoding))
                return encoding;

            try
            {
                encoding = Encoding.GetEncoding(this.ResponseEncoding);
            }
            catch (ArgumentException ex)
            {
                encoding = Encoding.UTF8;
                SnLog.WriteException(ex);
            }
            return encoding;
        }



        private JToken getErrorDocument(string exceptionMessage, string exceptionInnerMessage = "")
        {
            JToken result = null;
            return result;
        }


        public override string ToString()
        {
            string result = string.Empty;
			using (Stream streamData = this.Binary.GetStream())
			{

				byte[] bytes = new byte[streamData.Length];
				streamData.Position = 0;
				streamData.Read(bytes, 0, (int)streamData.Length);
				result = Encoding.UTF8.GetString(bytes);
			}

            return result;
        }

        //****************************************** Start of IHttpHandler *********************************************//
        public bool IsReusable
        {
            get { throw new NotImplementedException(); }
        }

                  public void ProcessRequest(HttpContext context)  
        {
            context.Response.Clear();
            context.Response.ClearHeaders();
            context.Response.ContentType = "application/json";
            context.Response.ContentEncoding = this.GetEncoding();

            context.Response.Cache.SetCacheability(CacheControlEnumValue);
            context.Response.Cache.SetMaxAge(new TimeSpan(0, 0, this.NumericMaxAge));
            context.Response.Cache.SetSlidingExpiration(true);  

            string receivestream = this.ToString();
            Byte[] byteArray = Encoding.UTF8.GetBytes(receivestream);

            context.Response.BufferOutput = true;
            context.Response.BinaryWrite(byteArray);
            context.Response.End();
        }

    }
}
