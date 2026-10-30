using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Anima.Launcher.Core;

/// <summary>Install the exact reviewed artifacts without running a second dependency solver.</summary>
public static class DependencyInstallation
{
    private sealed record PreparedPlan(string Identity, DependencyPackage[] Packages, string TransportIdentity = "");
    private sealed record BuiltWheel(string Identity, string FileName, long Size, string Sha256);
    private sealed record LocalWheel(DependencyPackage Package, string Path, long Size, string Sha256);
    public static string PlanIdentity(ComfyDependencySnapshot plan) => Hash(plan.PythonIdentity + "\n" + plan.ComfyCommit + "\n"
        + plan.NodeRequirementsIdentity + "\n" + string.Join("\n", plan.Constraints)
        + "\n" + string.Join("\n", plan.Packages.Select(p => p.Name + "==" + p.Version + "|" + p.Url + "|" + p.Sha256 + "|" + p.Commit + "|" + p.Subdirectory)));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static readonly string[] BuildEnvironmentNames =
    [
        "SAM2_BUILD_CUDA", "SAM2_BUILD_ALLOW_ERRORS", "TORCH_CUDA_ARCH_LIST", "CUDA_HOME", "CUDA_PATH", "CUDACXX", "CUDAHOSTCXX",
        "FORCE_CUDA", "CXX", "CC", "CMAKE_ARGS", "CMAKE_GENERATOR", "CMAKE_TOOLCHAIN_FILE", "CFLAGS", "CXXFLAGS", "CPPFLAGS", "LDFLAGS",
        "NVCC_PREPEND_FLAGS", "NVCC_APPEND_FLAGS", "NVCC_FLAGS", "MAX_JOBS", "PIP_CONFIG_SETTINGS", "SKBUILD_CMAKE_ARGS",
    ];

    private static Dictionary<string, string> CaptureBuildEnvironment(IDictionary<string, string> environment, Func<string, string?>? inherited = null)
    {
        inherited ??= Environment.GetEnvironmentVariable;
        var captured = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var pair in environment) captured[pair.Key] = pair.Value;
        foreach (var name in BuildEnvironmentNames)
            // A null value tells ProcessStartInfo to remove the variable. Capture absence too,
            // so later process-environment changes cannot make a build differ from its key.
            if (!captured.ContainsKey(name)) captured[name] = inherited(name)!;
        return captured;
    }

    internal static string WheelBuildIdentity(DependencyPackage package, ComfyDependencySnapshot plan,
        IDictionary<string, string> environment, Func<string, string?>? inherited = null)
    {
        var captured = CaptureBuildEnvironment(environment, inherited);
        var settings = BuildEnvironmentNames.Select(name => new { Name = name, Value = captured[name] });
        // Serialize values unambiguously, then persist only the digest, never raw flags or paths.
        return Hash(package.Name + "\n" + package.Version + "\n" + package.Url + "\n" + package.Commit + "\n" + package.Sha256
            + "\n" + package.Subdirectory + "\n" + plan.PythonIdentity + "\n" + string.Join("\n", plan.Constraints)
            + "\nsource-staging-v2=local-checkout\nno-build-isolation=" + (package.Name == "sam-2") + "\nbuild-environment-v1=" + System.Text.Json.JsonSerializer.Serialize(settings));
    }

    private static bool HasDigest(DependencyPackage package) => Regex.IsMatch(package.Sha256, "^[a-fA-F0-9]{64}$") && package.Size > 0;
    private static Artifact Artifact(DependencyPackage package) => new("dependency-" + package.Name, package.Url, package.Size, package.Sha256, "")
        { FileName = package.FileName, Version = package.Version };

    public static async Task InstallAsync(LauncherConfig config, ComfyDependencySnapshot plan, string runtimeRoot, Downloads downloads,
        IDictionary<string, string> environment, Func<CancellationToken, Task<string?>> resolveUv, CancellationToken ct,
        Action<string>? log = null, Action<DependencyProgress>? progress = null)
    {
        if (!plan.Resolved || plan.PlanVersion != 1) throw new InvalidDataException("缺少完整的固定依赖安装计划。");
        var reporter = new DependencyProgressReporter(progress, log);
        var watch = Stopwatch.StartNew();
        using var temporary = InstallationTemporaryDirectory.Enter(runtimeRoot);
        var env = new Dictionary<string, string>(environment) { ["TEMP"] = temporary.Path, ["TMP"] = temporary.Path };
        var sourceConfig = config.Clone();
        var transportIdentity = DependencyPlans.TransportIdentity(config);
        var reuseSource = plan.TransportIdentity == transportIdentity;
        if (reuseSource && DownloadSources.PipIndexes(config).Contains(plan.PackageIndex, StringComparer.OrdinalIgnoreCase)) sourceConfig.PipIndex = plan.PackageIndex;
        var indexes = Prefer(DownloadSources.PipIndexes(sourceConfig), reuseSource ? plan.PackageIndex : "");
        var gitSources = Prefer(DownloadSources.GitCandidates("https://github.com/", sourceConfig), reuseSource ? plan.GitSource : "");
        var identity = PlanIdentity(plan);
        var root = Path.Combine(runtimeRoot, "cache", "dependency-artifacts", identity);
        Directory.CreateDirectory(root);
        try
        {
            reporter.Phase("preparing", "正在检查固定依赖计划和可复用文件");
            DependencyPlans.ValidateEnvironment(plan, await DependencyPlans.InventoryAsync(config, ct));
            var saved = Path.Combine(root, "prepared.json");
            var packages = plan.Packages;
            var sameTransport = false;
            if (File.Exists(saved))
            {
                var cached = JsonFile.Read<PreparedPlan>(saved);
                if (cached.Identity != identity || cached.Packages.Length != packages.Length
                    || !cached.Packages.Zip(packages).All(p => p.First.Name == p.Second.Name && p.First.Version == p.Second.Version
                        && p.First.Url == p.Second.Url && p.First.Commit == p.Second.Commit && p.First.Subdirectory == p.Second.Subdirectory
                        && (p.Second.Sha256.Length == 0 || p.First.Sha256 == p.Second.Sha256)))
                    throw new InvalidDataException("依赖缓存计划与已确认版本不同，已保留。");
                packages = cached.Packages;
                sameTransport = cached.TransportIdentity == transportIdentity;
            }
            using var prepareGate = new SemaphoreSlim(4);
            packages = await Task.WhenAll(packages.Select(async package =>
            {
                await prepareGate.WaitAsync(ct);
                try
                {
                    if (package.Kind is "git" or "directory" || new Uri(package.Url).IsFile) return package;
                    if (HasDigest(package) && await Downloads.Verify(ArtifactCache.PathFor(runtimeRoot, Artifact(package)), package.Size, package.Sha256, ct)) return package;
                    if (sameTransport && HasDigest(package) && package.Sources.Length > 0) return package;
                    return await DependencyPackageSources.ResolveAsync(package, sourceConfig, ct);
                }
                finally { prepareGate.Release(); }
            }));
            JsonFile.Write(saved, new PreparedPlan(identity, packages, transportIdentity));
            var remaining = packages.Where(p => p.Kind is "wheel" or "archive" && HasDigest(p) && !new Uri(p.Url).IsFile)
                .Sum(p => ArtifactCache.RemainingBytes(runtimeRoot, Artifact(p)));
            Safety.FreeSpace(root, checked(remaining + 256L * 1024 * 1024));
            var received = new ConcurrentDictionary<string, long>();
            var speeds = new ConcurrentDictionary<string, double>();
            var lengths = packages.Where(p => HasDigest(p) && p.Kind is "wheel" or "archive").ToDictionary(p => p.Name, p => p.Size);
            var completed = 0;
            var downloadCount = packages.Count(p => p.Kind is "wheel" or "archive");
            // A partial sum is not the total for the complete dependency stage.
            var total = lengths.Count == downloadCount ? lengths.Values.Sum() : 0;
            var files = new ConcurrentDictionary<string, string>();
            reporter.Phase("downloading", "正在并发下载并校验依赖包");
            var transferWatch = Stopwatch.StartNew();
            using var downloadsStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var transferGate = new SemaphoreSlim(4);
            var fetches = packages.Where(p => p.Kind is "wheel" or "archive").Select(async package =>
            {
                var token = downloadsStop.Token;
                await transferGate.WaitAsync(token);
                try
                {
                    var path = await AcquireFileAsync(package, runtimeRoot, config, downloads, env, indexes, gitSources, temporary.Path, reporter,
                        new DownloadInlineProgress(value =>
                        {
                            if (lengths.ContainsKey(package.Name)) received[package.Name] = Math.Clamp(value.Received, 0, package.Size);
                            if (value.Phase == "Downloading" && value.Speed is >= 0 and var speed && double.IsFinite(speed)) speeds[package.Name] = speed;
                            else speeds.TryRemove(package.Name, out _);
                            reporter.Report(new("downloading", "正在下载并校验依赖包") { CurrentPackage = package.Name, Source = value.Source ?? "",
                                Received = total > 0 ? received.Values.Sum() : null, Total = total > 0 ? total : null,
                                BytesPerSecond = speeds.IsEmpty ? null : speeds.Values.Sum(),
                                CompletedPackages = Volatile.Read(ref completed), TotalPackages = downloadCount });
                        }), token);
                    files[package.Name] = path;
                    if (lengths.ContainsKey(package.Name)) received[package.Name] = package.Size;
                    Interlocked.Increment(ref completed);
                }
                catch { downloadsStop.Cancel(); throw; }
                finally { speeds.TryRemove(package.Name, out _); transferGate.Release(); }
            }).ToArray();
            // WhenAny would abandon writers. Always join every child before pause/failure returns.
            await Task.WhenAll(fetches);
            // Adopt legacy/source downloads into our public content cache, without depending on pip's private cache layout.
            for (var i = 0; i < packages.Length; i++)
            {
                var package = packages[i];
                if (!files.TryGetValue(package.Name, out var path) || HasDigest(package) || new Uri(package.Url).IsFile) continue;
                await using var input = File.OpenRead(path);
                var sha = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
                var promoted = package with { Sha256 = sha, Size = input.Length, FileName = Path.GetFileName(path), Sources = [package.Url] };
                var cachedPath = await ArtifactCache.PrepareAsync(runtimeRoot, Artifact(promoted), ct);
                using var target = await DownloadTargetLease.AcquireAsync(cachedPath, ct);
                if (!await Downloads.Verify(cachedPath, promoted.Size, sha, ct))
                {
                    var staging = cachedPath + ".adopting-" + Guid.NewGuid().ToString("N");
                    File.Copy(path, staging);
                    ct.ThrowIfCancellationRequested();
                    File.Move(staging, cachedPath, true);
                }
                packages[i] = promoted; files[package.Name] = cachedPath;
            }
            JsonFile.Write(saved, new PreparedPlan(identity, packages, transportIdentity));
            reporter.Log($"依赖下载阶段完成：{completed} 个文件，{transferWatch.Elapsed.TotalSeconds:F2} 秒。");
            var wheels = new List<LocalWheel>();
            foreach (var package in packages)
            {
                ct.ThrowIfCancellationRequested();
                if (package.Kind == "wheel")
                    wheels.Add(await InspectWheelAsync(package, files[package.Name], ct));
                else
                {
                    reporter.Phase("building", "正在构建并缓存依赖包 · " + package.Name);
                    var source = package.Kind == "archive" ? files[package.Name]
                        : package.Kind == "git" ? await DependencySources.PrepareAsync(package.Url, package.Commit,
                            Path.Combine(runtimeRoot, "cache"), config, ct, reporter.Log, env)
                        : package.SourceDirectory;
                    if (string.IsNullOrWhiteSpace(source) || !File.Exists(source) && !Directory.Exists(source)) throw new IOException("缺少固定依赖源码：" + package.Name);
                    wheels.Add(await BuildWheelAsync(package, source, config, plan, runtimeRoot, temporary.Path, env, indexes, gitSources, reporter, ct));
                }
            }
            DependencyPlans.ValidateEnvironment(plan, await DependencyPlans.InventoryAsync(config, ct));
            reporter.Phase("verifying", "正在核对已完成依赖的文件完整性");
            var reusable = await InstalledPackagesAsync(config, plan, wheels, root, env, reporter, recordCompleted: false, ct);
            var pending = wheels.Where(wheel => !reusable.Contains(wheel.Package.Name)).ToArray();
            if (reusable.Count > 0) reporter.Log($"已核验并复用 {reusable.Count} 个完整依赖包，剩余 {pending.Length} 个需要安装。");
            var requirements = Path.Combine(root, "local-wheels.txt");
            File.WriteAllLines(requirements, pending.Select(wheel => wheel.Package.Name + " @ " + new Uri(wheel.Path).AbsoluteUri + " --hash=sha256:" + wheel.Sha256));
            var constraints = Path.Combine(root, "constraints.txt");
            File.WriteAllLines(constraints, plan.Constraints);
            string? uv = null;
            try { if (pending.Length > 0) uv = await resolveUv(ct); }
            catch (Exception error) when (error is not OperationCanceledException)
            { reporter.Log("快速本地安装工具不可用，使用 pip 安装同一批已校验文件：" + DiagnosticRedaction.Text(error.Message)); }
            if (pending.Length > 0)
            {
                reporter.Phase("installing", $"正在本地安装 {pending.Length} 个依赖包");
                var installWatch = Stopwatch.StartNew();
                env["UV_CACHE_DIR"] = Path.Combine(runtimeRoot, "cache", "uv");
                env["UV_PYTHON_DOWNLOADS"] = "never";
                if (uv is not null)
                    await Commands.Run(uv, ["pip", "install", "--python", config.Python, "--no-config", "--no-python-downloads", "--offline", "--no-index", "--no-deps",
                        "--no-build", "--link-mode", "copy", "--reinstall", "--require-hashes", "-c", constraints, "-r", requirements], config.ComfyRoot, reporter.Log, ct, env);
                else
                    await Commands.Run(config.Python, ["-m", "pip", "install", "--disable-pip-version-check", "--no-index", "--no-deps", "--force-reinstall", "--require-hashes",
                        "-c", constraints, "-r", requirements], config.ComfyRoot, reporter.Log, ct, env);
                reporter.Log($"依赖本地安装完成：{installWatch.Elapsed.TotalSeconds:F2} 秒。");
            }
            reporter.Phase("verifying", "正在验证依赖、固定版本与 GPU 运行时");
            await RestoreOriginsAsync(config, plan with { Packages = pending.Select(wheel => wheel.Package).ToArray() }, root, env, reporter, ct);
            DependencyPlans.ValidateEnvironment(plan, await DependencyPlans.InventoryAsync(config, ct), installed: true);
            await Commands.Run(config.Python, ["-m", "pip", "check"], config.ComfyRoot, reporter.Log, ct, env);
            if (pending.Length > 0) await InstalledPackagesAsync(config, plan, wheels, root, env, reporter, recordCompleted: true, ct);
            reporter.Complete("依赖包已安装并通过校验");
            reporter.Log($"固定依赖计划完成：{watch.Elapsed.TotalSeconds:F2} 秒。");
        }
        catch (OperationCanceledException) { reporter.Pause("依赖安装已暂停，已校验文件保留"); throw; }
        catch (Exception error) { reporter.Fail("依赖安装失败：" + error.Message); throw; }
    }

    public static string[] Prefer(IEnumerable<string> candidates, string preferred)
    {
        var values = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return values.Contains(preferred, StringComparer.OrdinalIgnoreCase)
            ? new[] { preferred }.Concat(values.Where(v => !v.Equals(preferred, StringComparison.OrdinalIgnoreCase))).ToArray() : values;
    }

    private static async Task<HashSet<string>> InstalledPackagesAsync(LauncherConfig config, ComfyDependencySnapshot plan,
        IReadOnlyCollection<LocalWheel> wheels, string root, IDictionary<string, string> environment,
        DependencyProgressReporter reporter, bool recordCompleted, CancellationToken ct)
    {
        var receipt = Path.Combine(root, "installed.json");
        if (!recordCompleted && !File.Exists(receipt)) return [];
        var request = Path.Combine(root, "installed-check.json");
        var result = Path.Combine(root, "installed-check-result.json");
        var baseline = DependencyPlans.Versions(plan.EnvironmentPackages);
        JsonFile.Write(request, new { Identity = PlanIdentity(plan), plan.PythonIdentity,
            Gpu = baseline.Where(pair => pair.Key is "torch" or "torchvision" or "torchaudio").ToDictionary(pair => pair.Key, pair => pair.Value),
            Packages = wheels.Select(wheel => new { wheel.Package.Name, wheel.Package.Version, wheel.Sha256,
                OriginIdentity = Hash(System.Text.Json.JsonSerializer.Serialize(new { wheel.Package.Direct, wheel.Package.Kind,
                    wheel.Package.Url, wheel.Package.Commit, wheel.Package.Subdirectory })) }).ToArray() });
        // A receipt is written only after successful installation, provenance repair,
        // environment validation and pip check. On resume every recorded file is hashed
        // again; a matching version alone never authorizes skipping a package.
        const string script = """
import base64,csv,hashlib,importlib.metadata as m,json,os,pathlib,stat,sys
from concurrent.futures import ThreadPoolExecutor
request=json.load(open(sys.argv[1],encoding='utf-8-sig')); receipt=pathlib.Path(sys.argv[2]); result=pathlib.Path(sys.argv[3]); save=sys.argv[4]=='record'
prefix=os.path.realpath(sys.prefix); prefixkey=os.path.normcase(prefix).rstrip('\\/')+os.sep
binding={'schemaVersion':1,'identity':request['identity'],'pythonIdentity':request['pythonIdentity'],'python':os.path.normcase(os.path.realpath(sys.executable)),'pythonVersion':sys.version,'gpu':request['gpu']}
for name,version in request['gpu'].items():
 if m.version(name)!=version: raise RuntimeError('GPU runtime changed: '+name)
old={}
if not save:
 try:
  stored=json.loads(receipt.read_text(encoding='utf-8'))
  if isinstance(stored,dict) and all(stored.get(k)==v for k,v in binding.items()): old=stored['packages']
 except (OSError,ValueError,KeyError,TypeError): pass
# Resolve each parent once. File reparse points are rejected before opening, so a
# cached parent cannot turn a filename into an unchecked redirect outside Python.
class UnsafeInstalledPath(RuntimeError): pass
parents={}; paths={}
def locate(base,relative):
 key=(base,relative)
 if key not in paths:
  lexical=os.path.abspath(os.path.join(base,relative)); parent,name=os.path.split(lexical)
  if parent not in parents: parents[parent]=os.path.realpath(parent)
  path=os.path.join(parents[parent],name)
  if not os.path.normcase(path).startswith(prefixkey): raise UnsafeInstalledPath('File outside selected environment')
  paths[key]=path
 return paths[key]
def regular(path):
 info=os.lstat(path)
 if stat.S_ISLNK(info.st_mode) or getattr(info,'st_file_attributes',0)&getattr(stat,'FILE_ATTRIBUTE_REPARSE_POINT',1024) or not stat.S_ISREG(info.st_mode):
  raise UnsafeInstalledPath('Installed file is not a regular file')
# Read each distribution's metadata once, and freeze its declared paths before
# starting workers. Only declarations from this exact reviewed batch authorize a
# shared file (for example opencv-python and opencv-python-headless).
packages={}; errors={}; owners={}
for p in request['packages']:
 try:
  d=m.distribution(p['name'])
  if d.version!=p['version']: raise ValueError('Version changed')
  base=str(d.locate_file('')); rows=list(csv.reader((d.read_text('RECORD') or '').splitlines()))
  metadata=[row[0] for row in rows if len(row)==3 and row[0].replace('\\','/').endswith('.dist-info/METADATA')]
  if len(metadata)!=1: raise ValueError('Incomplete installed metadata')
  record=locate(base,os.path.join(os.path.dirname(metadata[0]),'RECORD')); regular(record)
  with open(record,'rb') as source: data=source.read()
  rows=list(csv.reader(data.decode('utf-8').splitlines())); entries=[]; seen=set()
  for row in rows:
   if len(row)!=3: raise ValueError('Invalid installed RECORD')
   relative,digest,size=row; path=locate(base,relative); key=os.path.normcase(path)
   if key in seen: raise ValueError('Duplicate installed path')
   seen.add(key)
   if key==os.path.normcase(record): continue
   if not digest and path.endswith('.pyc'): continue
   algorithm=digest.split('=',1)[0] if digest else 'sha256'
   if algorithm not in ('sha256','sha384','sha512'): raise ValueError('Unsupported RECORD hash')
   entries.append((relative,digest,size,path,key))
  if os.path.normcase(record) not in seen or not entries: raise ValueError('Incomplete installed RECORD')
  packages[p['name']]={'record':os.path.relpath(record,prefix),'recordSha256':hashlib.sha256(data).hexdigest(),'entries':entries}
  for relative,digest,size,path,key in entries:
   if digest: owners.setdefault(key,[]).append({'name':p['name'],'hash':digest,'size':size})
 except (OSError,ValueError,KeyError,TypeError,m.PackageNotFoundError) as error: errors[p['name']]=error
owners={key:sorted(value,key=lambda owner:owner['name']) for key,value in owners.items() if len({owner['name'] for owner in value})>1}
selected=[]
for p in request['packages']:
 previous=old.get(p['name']) if isinstance(old,dict) else None
 if save or isinstance(previous,dict) and previous.get('wheelSha256')==p['sha256'] and previous.get('originIdentity')==p['originIdentity']: selected.append(p)
def hash_file(path,algorithms):
 # Always read the bytes. Size and mtime never authorize skipping content checks.
 regular(path); hashers={name:hashlib.new(name) for name in algorithms}; length=0
 with open(path,'rb') as source:
  while True:
   chunk=source.read(1024*1024)
   if not chunk: break
   length+=len(chunk)
   for hasher in hashers.values(): hasher.update(chunk)
 return length,{name:base64.urlsafe_b64encode(hasher.digest()).rstrip(b'=').decode('ascii') for name,hasher in hashers.items()},hashers['sha256'].hexdigest()
valid=[]; snapshots={}
# Shared paths use the same freshly read bytes. Four workers bound outstanding
# file IO and memory, without allowing stat-only or cross-invocation hash caches.
with ThreadPoolExecutor(max_workers=4) as workers:
 jobs={}
 for p in selected:
  for relative,digest,size,path,key in packages.get(p['name'],{}).get('entries',[]):
   if key not in jobs:
    algorithms={'sha256',digest.split('=',1)[0] if digest else 'sha256'}|{owner['hash'].split('=',1)[0] for owner in owners.get(key,[])}
    jobs[key]=workers.submit(hash_file,path,algorithms)
 for p in selected:
  print('Checking installed files: '+p['name'],flush=True)
  try:
   if p['name'] in errors: raise errors[p['name']]
   package=packages[p['name']]; files=[]
   for relative,digest,size,path,key in package['entries']:
    length,actual,sha=jobs[key].result(); shared=owners.get(key,[])
    def matches(value,count):
     kind,hashvalue=value.split('=',1) if value else ('sha256','')
     return (not count or length==int(count)) and (not hashvalue or actual.get(kind)==hashvalue)
    if not matches(digest,size) and not any(matches(owner['hash'],owner['size']) for owner in shared): raise ValueError('Installed content changed')
    files.append({'path':relative,'size':length,'sha256':sha,'sharedOwners':shared})
   current={'version':p['version'],'wheelSha256':p['sha256'],'originIdentity':p['originIdentity'],'record':package['record'],'recordSha256':package['recordSha256'],'files':sorted(files,key=lambda f:f['path'])}
   if save or current==old[p['name']]: valid.append(p['name']); snapshots[p['name']]=current
  except (OSError,ValueError,KeyError,TypeError,m.PackageNotFoundError):
   if save: raise
if save:
 binding['packages']=snapshots; temporary=receipt.with_name(receipt.name+'.tmp')
 temporary.write_text(json.dumps(binding,sort_keys=True),encoding='utf-8'); os.replace(temporary,receipt)
result.write_text(json.dumps(valid),encoding='utf-8')
""";
        await Commands.Run(config.Python, ["-B", "-c", script, request, receipt, result, recordCompleted ? "record" : "check"],
            config.ComfyRoot, reporter.Log, ct, environment);
        return JsonFile.Read<string[]>(result).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task RestoreOriginsAsync(LauncherConfig config, ComfyDependencySnapshot plan, string root,
        IDictionary<string, string> environment, DependencyProgressReporter reporter, CancellationToken ct)
    {
        // Local wheel paths are a transport optimization, not a change to PEP 610 provenance.
        var origins = Path.Combine(root, "origins.json");
        JsonFile.Write(origins, plan.Packages);
        const string script = """
import base64,csv,hashlib,importlib.metadata as m,json,os,pathlib,sys,sysconfig
allowed=[pathlib.Path(sysconfig.get_path(k)).resolve() for k in ('purelib','platlib')]
for p in json.load(open(sys.argv[1],encoding='utf-8-sig')):
 d=m.distribution(p['name'])
 if d.version!=p['version']: raise RuntimeError('Installed version changed: '+p['name'])
 entries=[f for f in (d.files or []) if str(f).replace('\\','/').endswith('.dist-info/METADATA')]
 if len(entries)!=1: raise RuntimeError('Cannot identify installed metadata: '+p['name'])
 folder=pathlib.Path(d.locate_file(entries[0])).resolve().parent
 if not any(folder.is_relative_to(base) for base in allowed): raise RuntimeError('Metadata outside selected environment')
 path=folder/'direct_url.json'; record=folder/'RECORD'; key=folder.name+'/direct_url.json'
 origin=None
 if p['direct']:
  origin={'url':p['url']}
  if p['kind']=='git': origin['vcs_info']={'vcs':'git','commit_id':p['commit'],'requested_revision':p['commit']}
  elif p['kind']=='directory': origin['dir_info']={}
  else: origin['archive_info']={'hashes':{'sha256':p['sha256']}} if p['sha256'] else {}
  if p.get('subdirectory'): origin['subdirectory']=p['subdirectory']
 with record.open(newline='',encoding='utf-8') as f: rows=[r for r in csv.reader(f) if r and r[0].replace('\\','/')!=key]
 if origin is None:
  if path.exists(): path.unlink()
 else:
  data=(json.dumps(origin,sort_keys=True)+'\n').encode('utf-8'); path.write_bytes(data)
  digest=base64.urlsafe_b64encode(hashlib.sha256(data).digest()).rstrip(b'=').decode('ascii')
  rows.append([key,'sha256='+digest,str(len(data))])
 temp=record.with_name('RECORD.anima-tmp')
 with temp.open('w',newline='',encoding='utf-8') as f: csv.writer(f).writerows(rows)
 os.replace(temp,record)
print('Dependency origins verified')
""";
        if (plan.Packages.Length > 0) await Commands.Run(config.Python, ["-B", "-c", script, origins], config.ComfyRoot, reporter.Log, ct, environment);
    }

    private static async Task<string> AcquireFileAsync(DependencyPackage package, string runtime, LauncherConfig config, Downloads downloads,
        IDictionary<string, string> environment, string[] indexes, string[] gitSources, string temporary,
        DependencyProgressReporter reporter, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (new Uri(package.Url).IsFile)
        {
            var path = new Uri(package.Url).LocalPath;
            if (!HasDigest(package) || !await Downloads.Verify(path, package.Size, package.Sha256, ct)) throw new InvalidDataException("本地依赖文件与解析记录不同：" + package.Name);
            return path;
        }
        if (HasDigest(package))
        {
            var path = await ArtifactCache.PrepareAsync(runtime, Artifact(package), ct);
            await downloads.FetchDependencyAsync(package, path, config, progress, ct);
            return path;
        }
        reporter.Log("此包使用固定来源的兼容下载：" + package.Name);
        var folder = Path.Combine(temporary, "download-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var requirementUri = new UriBuilder(package.Url);
        var fragments = new List<string>();
        if (package.Sha256.Length > 0) fragments.Add("sha256=" + package.Sha256);
        if (package.Subdirectory.Length > 0) fragments.Add("subdirectory=" + Uri.EscapeDataString(package.Subdirectory));
        requirementUri.Fragment = string.Join("&", fragments);
        var requirement = requirementUri.Uri.AbsoluteUri;
        await ComfyDependencyResolver.WithPackageSourcesAsync(indexes, gitSources, async (index, gitSource) =>
        {
            var env = new Dictionary<string, string>(environment) { ["PIP_INDEX_URL"] = index };
            Installer.ConfigureGitEnvironment(env, gitSource);
            NetworkPolicySnapshot.Capture(config).AddEnvironment(env, new Uri(index));
            await Commands.Run(config.Python, ["-m", "pip", "download", "--no-deps", "--disable-pip-version-check", "--timeout", "20", "--retries", "1", "--dest", folder, requirement],
                config.ComfyRoot, reporter.Log, ct, env);
            return true;
        }, ct, reporter.Log, config);
        var file = Directory.GetFiles(folder).Single();
        await using var stream = File.OpenRead(file);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        if (package.Sha256.Length > 0 && !digest.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("依赖文件校验不一致。");
        return file;
    }

    private static async Task<LocalWheel> BuildWheelAsync(DependencyPackage package, string source, LauncherConfig config, ComfyDependencySnapshot plan,
        string runtime, string temporary, IDictionary<string, string> environment, string[] indexes, string[] gitSources, DependencyProgressReporter reporter, CancellationToken ct)
    {
        var buildEnvironment = CaptureBuildEnvironment(environment);
        var key = WheelBuildIdentity(package, plan, buildEnvironment);
        var cache = Path.Combine(runtime, "cache", "dependency-wheels", key);
        Directory.CreateDirectory(cache);
        using var lease = await DownloadTargetLease.AcquireAsync(cache, ct);
        var receiptPath = Path.Combine(cache, "wheel.json");
        if (File.Exists(receiptPath) && package.Kind != "directory")
        {
            var receipt = JsonFile.Read<BuiltWheel>(receiptPath);
            var wheel = Safety.Under(cache, receipt.FileName);
            if (receipt.Identity == key && await Downloads.Verify(wheel, receipt.Size, receipt.Sha256, ct)) return await InspectWheelAsync(package, wheel, ct);
        }
        var staging = Path.Combine(temporary, "wheel-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        source = await PrepareBuildSourceAsync(package, source, temporary, buildEnvironment, reporter.Log, ct);
        await ComfyDependencyResolver.WithPackageSourcesAsync(indexes, gitSources, async (index, gitSource) =>
        {
            var env = new Dictionary<string, string>(buildEnvironment, buildEnvironment.Comparer) { ["PIP_INDEX_URL"] = index };
            Installer.ConfigureGitEnvironment(env, gitSource);
            NetworkPolicySnapshot.Capture(config).AddEnvironment(env, new Uri(index));
            var args = new List<string> { "-m", "pip", "wheel", "--no-deps", "--disable-pip-version-check", "--timeout", "20", "--retries", "1", "--wheel-dir", staging };
            if (package.Name == "sam-2") args.Add("--no-build-isolation");
            args.Add(source);
            await Commands.Run(config.Python, args, config.ComfyRoot, reporter.Log, ct, env);
            return true;
        }, ct, reporter.Log, config);
        var built = await InspectWheelAsync(package, Directory.GetFiles(staging, "*.whl").Single(), ct);
        var destination = Safety.Under(cache, Path.GetFileName(built.Path));
        File.Copy(built.Path, destination, true);
        JsonFile.Write(receiptPath, new BuiltWheel(key, Path.GetFileName(destination), built.Size, built.Sha256));
        return built with { Path = destination };
    }

    // Build tools still create MAX_PATH-limited intermediate directories on Windows. Never
    // build in the immutable (and potentially deep) source cache. A local Git clone also
    // excludes untracked build leftovers while retaining tracked build/dist and VCS versions.
    internal static async Task<string> PrepareBuildSourceAsync(DependencyPackage package, string source, string temporary,
        IDictionary<string, string> environment, Action<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(source))
            return package.Subdirectory.Length == 0 ? source
                : new Uri(Path.GetFullPath(source)).AbsoluteUri + "#subdirectory=" + Uri.EscapeDataString(package.Subdirectory);
        source = Path.GetFullPath(source);
        ValidateSourceTree(source, ct);
        var destination = Safety.Under(temporary, "source-" + Guid.NewGuid().ToString("N")[..8]);
        if (package.Kind == "git")
        {
            Safety.Commit(package.Commit);
            var git = environment.TryGetValue("PATH", out var search)
                ? search.Split(Path.PathSeparator).Select(dir => Path.Combine(dir.Trim('"'), "git.exe")).FirstOrDefault(File.Exists)
                : Commands.Find("git.exe");
            git ??= "git.exe";
            var env = new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);
            // This operation has no remote fallback. Ignore user Git redirects, filters,
            // templates, hooks and object-directory overrides; only this local cache is read.
            foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY",
                "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CONFIG", "GIT_CONFIG_PARAMETERS", "GIT_TEMPLATE_DIR", "GIT_SHALLOW_FILE" }) env[name] = null!;
            env["GIT_CONFIG_COUNT"] = "0"; env["GIT_CONFIG_NOSYSTEM"] = "1";
            env["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            env["GIT_ALLOW_PROTOCOL"] = "file"; env["GIT_NO_LAZY_FETCH"] = "1";
            env["GIT_NO_REPLACE_OBJECTS"] = "1";
            var emptyHooks = Safety.Under(temporary, "empty-git-hooks");
            Directory.CreateDirectory(emptyHooks);
            var commonArgs = new[] { "-c", "core.hooksPath=" + emptyHooks, "-c", "protocol.file.allow=always",
                "-c", "core.longpaths=true", "-c", "core.symlinks=false" };
            async Task<string> Run(string cwd, params string[] args) =>
                await Commands.Run(git, commonArgs.Concat(args), cwd, null, ct, env);

            var rootGit = Path.Combine(source, ".git");
            if (!Directory.Exists(rootGit)) throw new InvalidDataException("固定依赖源码缺少独立 Git 元数据。");
            async Task Checkout(string from, string to, string commit)
            {
                ct.ThrowIfCancellationRequested();
                var metadata = Path.Combine(from, ".git");
                if (File.Exists(metadata))
                {
                    var declaration = File.ReadAllText(metadata).Trim();
                    if (!declaration.StartsWith("gitdir: ", StringComparison.Ordinal)) throw new InvalidDataException("子模块 Git 元数据无效。");
                    metadata = Path.GetFullPath(declaration[8..], from);
                    if (!metadata.StartsWith(rootGit + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("子模块 Git 元数据超出固定源码缓存。");
                }
                if (!Directory.Exists(metadata) || File.Exists(Path.Combine(metadata, "commondir"))
                    || File.Exists(Path.Combine(metadata, "objects", "info", "alternates"))
                    || File.Exists(Path.Combine(metadata, "objects", "info", "http-alternates"))
                    || Directory.Exists(Path.Combine(metadata, "objects", "pack"))
                        && Directory.EnumerateFiles(Path.Combine(metadata, "objects", "pack"), "*.promisor").Any())
                    throw new InvalidDataException("固定源码的 Git 对象依赖外部目录或远程内容，不能离线构建。");
                var actual = (await Run(from, "rev-parse", "HEAD")).Trim();
                if (actual != commit) throw new InvalidDataException("固定依赖源码提交在构建前发生变化。");
                var entries = (await Run(from, "ls-tree", "-rz", "--full-tree", commit)).TrimEnd('\r', '\n')
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries);
                var modules = new List<(string Path, string Commit)>();
                foreach (var entry in entries)
                {
                    var separator = entry.IndexOf('\t');
                    if (separator < 0) throw new InvalidDataException("固定源码树条目无效。");
                    var fields = entry[..separator].Split(' ');
                    var relative = entry[(separator + 1)..];
                    _ = Safety.Under(from, relative); _ = Safety.Under(to, relative);
                    // Git for Windows normally materializes symlink blobs as ordinary
                    // text files. Keep that safe representation; never create OS links.
                    if (fields[0] == "160000") { Safety.Commit(fields[2]); modules.Add((relative, fields[2])); }
                }
                log?.Invoke("本地短目录检出固定源码（不联网） · " + package.Name + " @ " + commit);
                await Run(temporary, "clone", "--local", "--no-hardlinks", "--no-checkout", "--template=", "--", from, to);
                await Run(to, "checkout", "--detach", commit);
                if ((await Run(to, "rev-parse", "HEAD")).Trim() != commit) throw new InvalidDataException("临时源码提交与固定计划不同。");
                foreach (var module in modules)
                    await Checkout(Safety.Under(from, module.Path), Safety.Under(to, module.Path), module.Commit);
            }
            await Checkout(source, destination, package.Commit);
        }
        else
        {
            // Local directory requirements may intentionally contain build/dist or metadata.
            // Preserve their complete safe tree instead of guessing which files are generated.
            void Copy(string from, string to)
            {
                ct.ThrowIfCancellationRequested(); Directory.CreateDirectory(to);
                // A worktree gitfile/alternate would still point back into another tree
                // after copying. Such directory requirements need a self-contained source.
                if (File.Exists(Path.Combine(from, ".git")) || Path.GetFileName(from).Equals(".git", StringComparison.OrdinalIgnoreCase)
                    && (File.Exists(Path.Combine(from, "commondir")) || File.Exists(Path.Combine(from, "objects", "info", "alternates"))
                        || File.Exists(Path.Combine(from, "objects", "info", "http-alternates"))))
                    throw new InvalidDataException("目录依赖的 Git 元数据指向其他目录，不能创建独立构建副本。");
                foreach (var entry in Directory.EnumerateFileSystemEntries(from))
                {
                    ct.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("依赖源码含重解析点，未复制。");
                    var target = Safety.Under(to, Path.GetFileName(entry));
                    if ((attributes & FileAttributes.Directory) != 0) Copy(entry, target);
                    else File.Copy(entry, target);
                }
            }
            Copy(source, destination);
        }
        ValidateSourceTree(destination, ct);
        return package.Subdirectory.Length == 0 ? destination : Safety.Under(destination, package.Subdirectory);
    }

    private static void ValidateSourceTree(string root, CancellationToken ct)
    {
        for (var parent = Path.GetFullPath(root); parent is not null; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("依赖源码路径含重解析点。");
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("依赖源码含重解析点。");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
    }

    private static async Task<LocalWheel> InspectWheelAsync(DependencyPackage package, string path, CancellationToken ct)
    {
        using (var zip = ZipFile.OpenRead(path))
        {
            var entry = zip.Entries.Single(e => e.FullName.EndsWith(".dist-info/METADATA", StringComparison.Ordinal));
            using var reader = new StreamReader(entry.Open());
            var metadata = await reader.ReadToEndAsync(ct);
            var name = Regex.Match(metadata, @"(?m)^Name: (.+)\r?$").Groups[1].Value.Trim();
            var version = Regex.Match(metadata, @"(?m)^Version: (.+)\r?$").Groups[1].Value.Trim();
            if (ComfyDependencyResolver.NormalizeName(name) != package.Name || version != package.Version) throw new InvalidDataException("wheel 内容与解析版本不同：" + package.Name);
        }
        await using var stream = File.OpenRead(path);
        return new(package, Path.GetFullPath(path), stream.Length, Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant());
    }
}
