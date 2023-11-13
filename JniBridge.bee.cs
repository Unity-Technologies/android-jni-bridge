using System;
using System.Collections.Generic;
using System.Text;
using Bee.BuildTools;
using Bee.Core;
using Bee.Core.Stevedore;
using Bee.NativeProgramSupport;
using Bee.Toolchain.Android;
using Bee.Toolchain.GNU;
using Bee.Tools;
using NiceIO;
using Bee.ProjectGeneration.VisualStudio;
using Bee.VisualStudioSolution;
using System.Linq;

/**
 * Required environment variables:
 *   - ANDROID_SDK_ROOT (optional) pointing to directory with Android SDK; will be downloaded from Stevedore otherwise
 *   - JAVA_HOME (optional) pointing to Java install; will be downloaded from Stevedore otherwise
 * Build targets to pass bee:
 *   - apigenerator - build the API generator
 *   - jnibridge - build the jnibridge.jar file
 *   - generate:jnibridge:android - generate C++ code for Android
 *   - generate:jnibridge:osx - generate C++ code for MaxOS (used by test program)
 *   - build:android:armeabi-v7a
 *   - build:android:arm64-v8a
 *   - build:android:x86
 *   - build:android:x86_64
 *   - build:android:zip - builds all 4 android archs above and zips them (THIS IS THE MAIN ONE)
 *   - build:osx:x86_64
 *   - build:osx:test - builds the one above and the test program (run ./build/osx/test afterwards to run tests)
 *   - build:windows:x86_64
 *   - build:windows:test - builds the one above and the test program (run build\windows\runtests.cmd afterwards to run tests)
 *   - projectfiles - generates IDE projects
 */


class JniBridge
{
    const string kAndroidApi = "android-31";
    const string kJavaVersion = "17";

    static class Platform
    {
        public const string OSX = "osx";
        public const string Windows = "windows";
    }

    static readonly string[] kAndroidApiClasses = new[]
    {
        "::android::Manifest_permission",
        "::android::R_attr",
        "::android::app::Activity",
        "::android::app::AlertDialog_Builder",
        "::android::app::NotificationManager",
        "::android::app::Presentation",
        "::android::content::Context",
        "::android::graphics::Color",
        "::android::graphics::ImageFormat",
        "::android::graphics::drawable::ColorDrawable",
        "::android::hardware::display::DisplayManager",
        "::android::hardware::Camera",
        "::android::hardware::input::InputManager",
        "::android::hardware::GeomagneticField",
        "::android::location::LocationManager",
        "::android::hardware::SensorManager",
        "::android::media::AudioAttributes_Builder",
        "::android::media::AudioFocusRequest_Builder",
        "::android::media::AudioManager",
        "::android::media::MediaCodec",
        "::android::media::MediaCodec::BufferInfo",
        "::android::media::MediaExtractor",
        "::android::media::MediaFormat",
        "::android::media::MediaRouter",
        "::android::net::ConnectivityManager",
        "::android::net::wifi::WifiManager",
        "::android::os::Build",
        "::android::os::Build_VERSION",
        "::android::os::HandlerThread",
        "::android::os::Environment",
        "::android::os::PowerManager",
        "::android::os::Process",
        "::android::os::Vibrator",
        "::android::provider::Settings_Global",
        "::android::provider::Settings_Secure",
        "::android::provider::Settings_System",
        "::android::telephony::TelephonyManager",
        "::android::telephony::SubscriptionManager",
        "::android::telephony::SubscriptionInfo",
        "::android::view::Choreographer",
        "::android::view::Display",
        "::android::view::Gravity",
        "::android::view::SurfaceView",
        "::android::view::WindowManager",
        "::android::webkit::MimeTypeMap",
        "::android::widget::CheckBox",
        "::android::widget::CompoundButton_OnCheckedChangeListener",
        "::android::widget::ProgressBar",
        "::java::lang::Character",
        "::java::lang::System",
        "::java::lang::SecurityException",
        "::java::lang::NoSuchMethodError",
        "::java::lang::ClassCastException",
        "::java::lang::UnsatisfiedLinkError",
        "::java::io::FileNotFoundException",
        "::java::net::HttpURLConnection",
        "::java::nio::channels::Channels",
        "::java::util::HashSet",
        "::java::util::Map_Entry",
        "::java::util::NoSuchElementException",
        "::java::util::Scanner",
        "::java::util::zip::ZipFile",
        "::javax::net::ssl::X509TrustManager",
        "::javax::net::ssl::TrustManagerFactory",
        "::java::security::KeyStore",
        "::com::google::android::gms::ads::identifier::AdvertisingIdClient",
        "::com::google::android::gms::common::GooglePlayServicesAvailabilityException",
        "::com::google::android::gms::common::GooglePlayServicesNotAvailableException",
    };

    private static readonly string[] kDesktopClasses = new[]
    {
        "::java::lang::System",
        "::java::lang::UnsupportedOperationException",
    };

    private static NPath[] HeaderFiles => NPath.CurrentDirectory.Files("*.h");
    private static NPath[] CppFiles => NPath.CurrentDirectory.Files("*.cpp");
    private static NPath[] JavaFiles => NPath.CurrentDirectory.Combine("jnibridge/bitter/jnibridge").Files("*.java");

    static void Main()
    {
    	using var _ = new BuildProgramContext();
        var codegen = CodeGen.Release;

        var jdk = SetupJava();
        var sdk = SetupAndroidSdk();
        var gps = SetupGooglePlayServices();

        var apiGenerator = SetupApiGenerator(jdk);
        var jnibridgeJar = SetupJniBridgeJar(jdk);
        var generatedFilesAndroid = SetupSourceGeneration(jdk, apiGenerator, jnibridgeJar, GetAndroidSourceGenerationParams(sdk, gps));
        var generatedFilesMacOS = SetupSourceGeneration(jdk, apiGenerator, jnibridgeJar, GetMacOSSourceGenerationParams(jdk));
        var generatedFilesWindows = SetupSourceGeneration(jdk, apiGenerator, jnibridgeJar, GetWindowsSourceGenerationParams(jdk));

        var androidZip = new ZipArchiveContents();
        var versionFile = VersionControl.SetupWriteRevisionInfoFile(new NPath("artifacts").Combine("build.txt"));
        var androidToolchains = GetAndroidToolchains();
        var nativePrograms = new List<NativeProgram>();
        foreach (var toolchain in androidToolchains)
        {
            var androidConfig = new NativeProgramConfiguration(codegen, toolchain, false);
            var np = SetupJniBridgeStaticLib(generatedFilesAndroid, androidConfig, GetAndroidStaticLibParams(toolchain, codegen), androidZip);
            nativePrograms.Add(np);
        }
        var headers = SetupJniBridgeHeaders(generatedFilesAndroid, "android");
        foreach (var header in headers)
        {
            androidZip.AddFileToArchive(header, new NPath("include").Combine("jnibridge", header.FileName));
        }
        androidZip.AddFileToArchive(jnibridgeJar);
        androidZip.AddFileToArchive(versionFile);
        androidZip.AddFileToArchive("LICENSE");


        var codegenForTests = CodeGen.Debug;
        var osxToolchain = ToolChain.Store.Mac().Sdk_10_13().x64();
        var osxConfig = new NativeProgramConfiguration(codegenForTests, osxToolchain, false);
        var osxStaticLib = SetupJniBridgeStaticLib(generatedFilesMacOS, osxConfig, GetMacOSStaticLibParams(osxToolchain, jdk));
        SetupTestProgramOsx(osxToolchain, osxStaticLib, codegenForTests, generatedFilesMacOS, jdk);

        var windowsToolchain = ToolChain.Store.Windows().VS2019().Sdk_18362().x64();
        var windowsConfig = new NativeProgramConfiguration(codegenForTests, windowsToolchain, false);
        var windowsStaticLib = SetupJniBridgeStaticLib(generatedFilesWindows, windowsConfig, GetWindowsStaticLibParams(windowsToolchain, jdk));
        var windowsTestProgram = SetupTestProgramWindows(windowsToolchain, windowsStaticLib, codegenForTests, generatedFilesWindows, jdk,
            out var targetExecutable, out var arguments, out var workingDirectory);

        var androidZipPath = "build/jnibridge-android.7z";
        ZipTool.SetupPack(androidZipPath, androidZip);
        Backend.Current.AddAliasDependency("build:android:zip", androidZipPath);

        SetupGeneratedProjects(nativePrograms, windowsTestProgram, generatedFilesWindows, targetExecutable, arguments, workingDirectory);
    }

    static Jdk SetupJava()
    {
        var jdk = Jdk.UserDefault;
        if (jdk != null)
            return jdk;
        var openJdk = StevedoreArtifact.UnityInternal(HostPlatform.Pick(
            linux:   "open-jdk-linux-x64/jdk17.0.9-9_8d1cbcce56285f3146cf7761353a643fe573b39e45bd94f35590dca39277f667.zip",
            mac:     "open-jdk-mac-x64/jdk17.0.9-9_388f7edd2524a9235650fa7cf531302e4676b80526b2d6a0fa199d030779169d.zip",
            windows: "open-jdk-windows-x64/jdk17.0.9-9_f12c2989c2f749b13282640a12d7d624097f6c2d45144d87331f21ad352ab63e.zip"
        ));
        return new Jdk(openJdk.Path.ResolveWithFileSystem());
    }

    static NPath SetupAndroidSdk()
    {
        var sdk = Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
        if (!string.IsNullOrEmpty(sdk))
        {
            var sdkNpath = new NPath(sdk);
            // use sdk from ANDROID_SDK_ROOT path only if it contains required API level, otherwise downlaod from artifactory
            if (System.IO.File.Exists(sdkNpath.Combine("platforms", kAndroidApi, "android.jar").ToString()))
                return sdkNpath;
        }
        var sdkArtifact = StevedoreArtifact.UnityInternal(HostPlatform.Pick(
            linux:   "android-sdk-linux-x86_64/34.0.0_0a97a033af4e683716921230975eeec605001b4782c3c813c4bd1cd45478d701.7z",
            mac:     "android-sdk-darwin-x86_64/34.0.0_243d245a26b16b02ad9e589bfd78a993ef992c23732c3b03c9120abde6bfb045.7z",
            windows: "android-sdk-windows-x86_64/34.0.0_7b70575084ce12881d8d8c84e005136ab4a841003c59dff4d0a66e95d5be2781.7z"
        ));
        return sdkArtifact.Path.ResolveWithFileSystem();
    }

    static NPath SetupGooglePlayServices()
    {
        // Downloaded from https://dl.google.com/android/repository/google_play_services_3265130_r12.zip
        var gps = StevedoreArtifact.UnityInternal("google-play-services/3265130_r12_3a601d0d9366fe05c7a56855f2c906a8d703fd297f1c0f6cfdaf27b181be284c.zip");
        return gps.Path.ResolveWithFileSystem();
    }

    static AndroidNdkToolchain[] GetAndroidToolchains()
    {
        var ndk = ToolChain.Store.Android().r23b();
        return new[]
        {
            ndk.Armv7(),
            ndk.Arm64(),
            ndk.x86(),
            ndk.x64(),
        };
    }

    static NPath SetupApiGenerator(Jdk jdk)
    {
        var apiGeneratorDir = new NPath("apigenerator");
        return SetupJarForDirectory(jdk, apiGeneratorDir);
    }

    static NPath SetupJniBridgeJar(Jdk jdk)
    {
        var jnibridgeDir = new NPath("jnibridge");
        return SetupJarForDirectory(jdk, jnibridgeDir);
    }

    static NPath SetupJarForDirectory(Jdk jdk, NPath jarDir)
    {
        var jarname = jarDir.FileName;
        var apiGeneratorFiles = jarDir.Files("*.java", true);
        var classFileDir = new NPath($"artifacts/{jarname}");
        var apiGeneratorJar = new NPath($"build/{jarname}.jar");
        var classFiles = jdk.SetupCompilation(classFileDir, apiGeneratorFiles, new NPath[] { jarDir }, new NPath[0], targetVersion: kJavaVersion);
        jdk.SetupJar(new NPath[] { classFileDir }, classFiles, apiGeneratorJar);
        Backend.Current.AddAliasDependency(jarname, apiGeneratorJar);
        return apiGeneratorJar;
    }

    struct SourceGenerationParams
    {
        public string platformName;
        public NPath[] inputJars;
        public string[] classes;
    }

    static SourceGenerationParams GetAndroidSourceGenerationParams(NPath sdk, NPath gps)
    {
        var androidJar = sdk.Combine("platforms", kAndroidApi, "android.jar");
        var googlePlayServicesJar = gps.Combine("google-play-services", "libproject", "google-play-services_lib", "libs", "google-play-services.jar");

        return new SourceGenerationParams()
        {
            platformName = "android",
            inputJars = new[] {androidJar, googlePlayServicesJar},
            classes = kAndroidApiClasses,
        };
    }

    static SourceGenerationParams GetDesktopSourceGenerationParams(Jdk jdk, string platformName)
    {
        return new SourceGenerationParams()
        {
            platformName = platformName,
            inputJars = Array.Empty<NPath>(),
            classes = kDesktopClasses,
        };
    }

    static SourceGenerationParams GetMacOSSourceGenerationParams(Jdk jdk)
    {
        return GetDesktopSourceGenerationParams(jdk, Platform.OSX);
    }

    static SourceGenerationParams GetWindowsSourceGenerationParams(Jdk jdk)
    {
        return GetDesktopSourceGenerationParams(jdk, Platform.Windows);
    }

    static NPath SetupSourceGeneration(Jdk jdk, NPath apiGenerator, NPath jnibridgeJar, SourceGenerationParams genParams)
    {
        var destDir = new NPath("artifacts").Combine("generated", genParams.platformName);
        var actionName = "generate:jnibridge:" + genParams.platformName;
        var generatedFiles = new NPath[] { destDir.Combine("API.h") };

        var templateFiles = new NPath("templates").Files(new [] { "cpp", "h" });
        var inputs = new List<NPath>();
        inputs.AddRange(genParams.inputJars);
        inputs.Add(jnibridgeJar);
        inputs.Add(apiGenerator);
        inputs.AddRange(templateFiles);

        string inputJars = string.Empty;
        if (genParams.inputJars.Length > 0)
        {
            var builder = new StringBuilder();
            builder.Append('"').Append(genParams.inputJars[0]);
            for (int i = 1; i < genParams.inputJars.Length; ++i)
                builder.Append(';').Append(genParams.inputJars[i]);
            inputJars = builder.Append('"').ToString();
        }
        else if (genParams.platformName.Equals(Platform.OSX) || genParams.platformName.Equals(Platform.Windows))
        {
            // Specify -s flag to instruct apigenerator to look for system classes
            // if we are generating API classes for OSX or Windows platforms and don't specify any jar files
            inputJars = "-s";
        }
        var apiClassString = string.Join(" ", genParams.classes);
        
        Backend.Current.AddAction(
            actionName,
            generatedFiles,
            inputs.ToArray(),
            jdk.Java.InQuotes(SlashMode.Native),
            new[]
            {
                "-cp",
                apiGenerator.InQuotes(),
                "APIGenerator",
                destDir.InQuotes(),
                inputJars,
                apiClassString
            },
            targetDirectories:new []{destDir}
        );

        Backend.Current.AddAliasDependency(actionName, generatedFiles);
        return destDir;
    }

    struct StaticLibParams
    {
        public string libName;
        public string platformName;
        public string archName;
        public Action<NativeProgram> specialConfiguration;
    }

    static StaticLibParams GetAndroidStaticLibParams(ToolChain toolchain, CodeGen codegen)
    {
        var debugMode = codegen == CodeGen.Release ? DebugMode.None : DebugMode.IncludeDebugSymbols;
        Action<NativeProgram> specConfig =  (toolchain.Architecture is ARMv7Architecture)
            ? (np) =>
            {
                np.CompilerSettingsForAndroid().Add(c => c.WithThumb(true));
                np.CompilerSettingsForAndroid().Add(c => c.WithDebugMode(debugMode));
            }
            : (np) =>
            {
                np.CompilerSettingsForAndroid().Add(c => c.WithDebugMode(debugMode));
            };
        return new StaticLibParams()
        {
            libName = kAndroidApi,
            platformName = "android",
            archName = AndroidNdk.AbiFromArch(toolchain.Architecture),
            specialConfiguration = specConfig,
        };
    }

    static StaticLibParams GetMacOSStaticLibParams(ToolChain toolchain, Jdk jdk)
    {
        return new StaticLibParams()
        {
            libName = "testlib",
            platformName = Platform.OSX,
            archName = toolchain.Architecture.Name,
            specialConfiguration = (np) =>
            {
                np.IncludeDirectories.Add(jdk.JavaHome.Combine("include"));
                np.IncludeDirectories.Add(jdk.JavaHome.Combine("include", "darwin"));
            },
        };
    }

    static StaticLibParams GetWindowsStaticLibParams(ToolChain toolchain, Jdk jdk)
    {
        return new StaticLibParams()
        {
            libName = "testlib",
            platformName = Platform.Windows,
            archName = toolchain.Architecture.Name,
            specialConfiguration = (np) =>
            {
                np.IncludeDirectories.Add(jdk.JavaHome.Combine("include"));
                np.IncludeDirectories.Add(jdk.JavaHome.Combine("include", "win32"));
            },
        };
    }

    static NativeProgram SetupJniBridgeStaticLib(NPath generatedFilesDir, NativeProgramConfiguration config, StaticLibParams libParams, ZipArchiveContents targetArchive = null)
    {
        NPath[] generatedSources;
        if (!generatedFilesDir.DirectoryExists())
            generatedSources = new[] {generatedFilesDir.Combine("API.h")};
        else
            generatedSources = generatedFilesDir.Files("*.cpp");

        var buildDest = new NPath("build").Combine(libParams.platformName);

        var np = new NativeProgram(libParams.libName);
        np.Sources.Add(generatedSources);
        np.Sources.Add(CppFiles);
        np.IncludeDirectories.Add(generatedFilesDir);
        np.ExtraDependenciesForAllObjectFiles.Add(generatedFilesDir);
        if (libParams.specialConfiguration != null)
            libParams.specialConfiguration(np);

        var destDir = buildDest.Combine(libParams.archName);
        var target = np.SetupSpecificConfiguration(config, config.ToolChain.StaticLibraryFormat).DeployTo(destDir);

        var alias = $"build:{libParams.platformName}:{libParams.archName}";
        Backend.Current.AddAliasDependency(alias, target.Paths);
        if (targetArchive != null)
        {
            var destInArchive = destDir.RelativeTo("build");
            foreach (var file in target.Paths)
                targetArchive.AddFileToArchive(file, destInArchive.Combine(file.FileName));
        }

        return np;
    }

    static NPath[] SetupJniBridgeHeaders(NPath generatedFilesDir, string platformName)
    {
        var includeFiles = new List<NPath>();
        var includes = new NPath("build").Combine(platformName, "include", "jnibridge");

        includeFiles.Add(Backend.Current.SetupCopyFile(includes.Combine("API.h"), generatedFilesDir.Combine("API.h")));
        foreach (var file in HeaderFiles)
            includeFiles.Add(Backend.Current.SetupCopyFile(includes.Combine(file.FileName), file));

        var incs = includeFiles.ToArray();
        Backend.Current.AddAliasDependency($"includes:{platformName}", incs);
        return incs;
    }

    static void SetupTestProgramOsx(ToolChain toolchain, NativeProgram staticLib, CodeGen codegen, NPath generatedFilesDir, Jdk jdk)
    {
        var np = new NativeProgram("JNIBridgeTests");
        np.Sources.Add(new NPath("test").Files("*.cpp"));
        np.IncludeDirectories.Add(generatedFilesDir);
        np.IncludeDirectories.Add(jdk.JavaHome.Combine("include"));
        np.IncludeDirectories.Add(jdk.JavaHome.Combine("include", "darwin"));
        np.Libraries.Add(staticLib);
        var javaLibDir = jdk.JavaHome.Combine("lib", "server");
        np.Libraries.Add(new DynamicLibrary(javaLibDir.Combine("libjvm.dylib")));
        np.CompilerSettings().Add(c => c.WithCustomFlags(new [] {$"-Wl,-rpath,{javaLibDir}"}));
        
        var destDir = new NPath("build").Combine(Platform.OSX);
        var config = new NativeProgramConfiguration(codegen, toolchain, false);
        var target = np.SetupSpecificConfiguration(config, config.ToolChain.ExecutableFormat).DeployTo(destDir);
        
        Backend.Current.AddAliasDependency($"build:{Platform.OSX}:test", target.Paths);
    }

    static NativeProgram SetupTestProgramWindows(ToolChain toolchain, NativeProgram staticLib, CodeGen codegen, NPath generatedFilesDir, Jdk jdk, out NPath targetExecutable, out string arguments, out NPath workingDirectory)
    {
        var np = new NativeProgram("JNIBridgeTests");
        np.Sources.Add(new NPath("test").Files("*.cpp"));
        np.IncludeDirectories.Add(generatedFilesDir);
        np.IncludeDirectories.Add(jdk.JavaHome.Combine("include"));
        np.IncludeDirectories.Add(jdk.JavaHome.Combine("include", "win32"));
        np.Libraries.Add(staticLib);
        var javaLibDir = jdk.JavaHome.Combine("lib");
        np.Libraries.Add(new StaticLibrary(javaLibDir.Combine("jvm.lib")));

        var destDir = new NPath("build").Combine(Platform.Windows);
        var config = new NativeProgramConfiguration(codegen, toolchain, false);
        var target = np.SetupSpecificConfiguration(config, config.ToolChain.ExecutableFormat).DeployTo(destDir);

        workingDirectory = jdk.JavaHome.Combine("bin", "server").MakeAbsolute();
        targetExecutable = destDir.Combine(np.Name + ".exe").MakeAbsolute();
        arguments = "build/jnibridge.jar".ToNPath().MakeAbsolute().InQuotes();
        var script = destDir.Combine("runtests.cmd");
        Backend.Current.AddWriteTextAction(script, $@"echo off
echo Launching tests
cd {workingDirectory.InQuotes()}
{targetExecutable.InQuotes()} {arguments}
echo Exited with code %ERRORLEVEL%
exit %ERRORLEVEL%
");

        var targetPaths = new List<NPath>(target.Paths);
        targetPaths.Add(script);
        Backend.Current.AddAliasDependency($"build:{Platform.Windows}:test", targetPaths.ToArray());

        return np;
    }

    static string GetABI(Architecture architecture)
    {
        if (architecture == Architecture.Armv7)
            return "armeabi-v7a";
        if (architecture == Architecture.Arm64)
            return "arm64-v8a";
        if (architecture == Architecture.x86)
            return "x86";
        if (architecture == Architecture.x64)
            return "x86_64";
        throw new NotImplementedException(architecture.ToString());
    }

    static void SetupGeneratedProjects(IReadOnlyList<NativeProgram> programs, NativeProgram testingProgram, NPath generatedFilesDirectory,
        NPath executableForTests, string arguments, NPath workingDirectory)
    {
        // Not entirely sure, but I think there's should have been only NativeProgram with different architectures.
        // Yet we have 4
        // For now pick the first one, since it will be enough for editing purposes

        var nativeProgram = programs[0];
        var ndkToolchain = (AndroidNdkToolchain)nativeProgram.SetupConfigurations.FirstOrDefault().ToolChain;
        var ndkIncludePath = ndkToolchain.Sdk.Path.ResolveWithFileSystem().Combine("sysroot/usr/include");
        
        var jniBridgeNP = new NativeProgram("JNIBridge");
        nativeProgram.Sources.CopyTo(jniBridgeNP.Sources);
        nativeProgram.IncludeDirectories.CopyTo(jniBridgeNP.IncludeDirectories);
        nativeProgram.Defines.CopyTo(jniBridgeNP.Defines);

        jniBridgeNP.IncludeDirectories.Add(ndkIncludePath);

        var configs = new List<NativeProgramConfiguration>();
        foreach (var p in programs)
        {
            configs.AddRange(p.SetupConfigurations.ToArray());
        }

        jniBridgeNP.ValidConfigurations = configs;
        jniBridgeNP.OutputName.Set(string.Empty);

        var setAndroidSDKRoot = $"set ANDROID_SDK_ROOT={SetupAndroidSdk()}";
        jniBridgeNP.CommandToBuild.Set(c =>
        {
            var architecture = ((AndroidNdkToolchain)c.ToolChain).Architecture;
            return $@"{setAndroidSDKRoot}
bee build:android:{GetABI(architecture)}";
        });

        var jniBridgeSln = new VisualStudioSolution();
        foreach (var config in configs)
        {
            var architecture = ((AndroidNdkToolchain)config.ToolChain).Architecture;

            jniBridgeSln.Configurations.Add(new SolutionConfiguration(GetABI(architecture), (configurations, file) =>
            {
                var firstOrDefault = configurations.FirstOrDefault(c => c == config);
                return new Tuple<IProjectConfiguration, bool>(
                    firstOrDefault ?? configurations.First(),
                    true);
            }));
        }
        var extraFiles = new List<NPath>();
        extraFiles.AddRange(HeaderFiles);
        extraFiles.AddRange(CppFiles);
        extraFiles.AddRange(JavaFiles);

        var builder = new VisualStudioNativeProjectFileBuilder(jniBridgeNP, extraFiles);
        builder = jniBridgeNP.ValidConfigurations.Aggregate(
            builder,
            (current, c) => current.AddProjectConfiguration(c)
            );

        
        jniBridgeSln.Projects.Add(builder.DeployTo("JNIBridge.vcxproj"));

        testingProgram.CommandToBuild.Set(c =>
        {
            return @$"{setAndroidSDKRoot}
bee build:windows:test";
        });

        var extraFilesForTests = new List<NPath>();
        extraFilesForTests.AddRange(HeaderFiles);
        extraFilesForTests.AddRange(CppFiles);
        extraFilesForTests.AddRange(JavaFiles);
        // Includes generated files for Windows, we don't want those in JNIBridge project since it builds for Android
        if (generatedFilesDirectory.DirectoryExists())
            extraFilesForTests.AddRange(generatedFilesDirectory.Files(true));       

        builder = new VisualStudioNativeProjectFileBuilder(testingProgram, extraFilesForTests);
        builder = testingProgram.SetupConfigurations.Aggregate(
            builder,
            (current, c) => current.AddProjectConfiguration(c)
            );

        var jniBridgeTestsSln = new VisualStudioSolution();
        var vsProject = builder.DeployTo("JNIBridgeTests.vcxproj");
        jniBridgeTestsSln.Projects.Add(vsProject);

        var vsProjectUsersSettings = (vsProject.Path + ".user").ToNPath();
        GenerateVSUserSettings(vsProjectUsersSettings, executableForTests, arguments, workingDirectory);
        Backend.Current.AddAliasDependency("projectfiles", jniBridgeSln.Setup());
        Backend.Current.AddAliasDependency("projectfiles", jniBridgeTestsSln.Setup());
        Backend.Current.AddAliasDependency("projectfiles", vsProjectUsersSettings);
        Backend.Current.AddAliasDependency("projectfiles", generatedFilesDirectory);
    }

    static void GenerateVSUserSettings(NPath path, NPath executablePath, string arguments, NPath workingDirectory)
    {
        Backend.Current.AddWriteTextAction(path,
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""Current"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup Condition=""'$(Configuration)|$(Platform)'=='debug_Win64_VS2019_nonlump|x64'"">
    <LocalDebuggerCommand>{executablePath.MakeAbsolute()}</LocalDebuggerCommand>
    <DebuggerFlavor>WindowsLocalDebugger</DebuggerFlavor>
    <LocalDebuggerWorkingDirectory>{workingDirectory}</LocalDebuggerWorkingDirectory>
    <LocalDebuggerCommandArguments>{arguments}</LocalDebuggerCommandArguments>
  </PropertyGroup>
</Project>
");
    }
}
