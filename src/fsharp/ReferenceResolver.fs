// Copyright (c) Microsoft Corporation.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

#if INTERACTIVE
#load "../utils/ResizeArray.fs" "../absil/illib.fs"
#else
module Microsoft.FSharp.Compiler.ReferenceResolver 
#endif

open System
open System.IO
open System.Reflection
open Microsoft.Win32

exception ResolutionFailure
open Microsoft.FSharp.Compiler.AbstractIL.Internal.Library

type ResolutionEnvironment = 
    /// Indicates a script or source being compiled
    | CompileTimeLike 
    /// Indicates a script or source being interpreted
    | RuntimeLike 
    /// Indicates a script or source being edited
    | DesignTimeLike

type ResolvedFile = 
    { /// Item specification.
        itemSpec:string
        /// Prepare textual information about where the assembly was resolved from, used for tooltip output
        prepareToolTip: string * string -> string
        /// Round-tripped baggage 
        baggage:string
        }

    override this.ToString() = sprintf "ResolvedFile(%s)" this.itemSpec

type Resolver =
    /// Get the "v4.5.1"-style moniker for the highest installed .NET Framework version.
    /// This is the value passed back to Resolve if no explicit "mscorlib" has been given.
    ///
    /// Note: If an explicit "mscorlib" is given, then --noframework is being used, and the whole ReferenceResolver logic is essentially
    /// unused.  However in the future an option may be added to allow an expicit specification of
    /// a .NET Framework version to use for scripts.
    abstract HighestInstalledNetFrameworkVersion : unit -> string
    
    /// Get the Reference Assemblies directory for the .NET Framework (on Windows)
    /// This is added to the default resolution path for 
    /// design-time compilations.
    abstract DotNetFrameworkReferenceAssembliesRootDirectory : string

    /// Perform assembly resolution on the given references under the given conditions
    abstract Resolve :
        resolutionEnvironment: ResolutionEnvironment *
        // The actual reference paths or assemby name text, plus baggage
        references:(string (* baggage *) * string)[] *  
        // e.g. v4.5.1
        targetFrameworkVersion:string *
        targetFrameworkDirectories:string list *
        targetProcessorArchitecture:string *
        outputDirectory: string * 
        fsharpCoreDir:string *
        explicitIncludeDirs:string list *
        implicitIncludeDir:string *
        logmessage:(string->unit) *
        logwarning:(string->string->unit) *
        logerror:(string->string->unit)
            -> ResolvedFile[]

let SimulatedMSBuildResolver =
    { new Resolver with 
        member __.HighestInstalledNetFrameworkVersion() = "v4.5"
        member __.DotNetFrameworkReferenceAssembliesRootDirectory = 
            if System.Environment.OSVersion.Platform = System.PlatformID.Win32NT then 
                let PF = 
                    match Environment.GetEnvironmentVariable("ProgramFiles(x86)") with
                    | null -> Environment.GetEnvironmentVariable("ProgramFiles")  // if PFx86 is null, then we are 32-bit and just get PF
                    | s -> s 
                PF + @"\Reference Assemblies\Microsoft\Framework\.NETFramework"
            else
                ""

        member __.Resolve(resolutionEnvironment, references, targetFrameworkVersion, targetFrameworkDirectories, targetProcessorArchitecture,                
                            outputDirectory, fsharpCoreDir, explicitIncludeDirs, implicitIncludeDir, logMessage, logWarning, logError) =

            let registrySearchPaths() = 
              [ let registryKey = @"Software\Microsoft\.NetFramework";
                use key = Registry.LocalMachine.OpenSubKey(registryKey)
                match key with 
                | null -> ()
                | _ -> 
                for subKeyName in key.GetSubKeyNames() do
                    use subKey = key.OpenSubKey(subKeyName)
                    use subSubKey = subKey.OpenSubKey("AssemblyFoldersEx")
                    match subSubKey with 
                    | null -> ()
                    | _ -> 
                        for subSubSubKeyName in subSubKey.GetSubKeyNames() do
                            use subSubSubKey = subSubKey.OpenSubKey(subSubSubKeyName)
                            match subSubSubKey.GetValue(null) with 
                            | :? string as s -> yield s
                            | _ -> () 
                use subSubKey = key.OpenSubKey("AssemblyFolders")
                match subSubKey with 
                | null -> ()
                | _ -> 
                    for subSubSubKeyName in subSubKey.GetSubKeyNames() do
                        let subSubSubKey = subSubKey.OpenSubKey(subSubSubKeyName)
                        match subSubSubKey.GetValue(null) with 
                        | :? string as s -> yield s
                        | _ -> ()  ]


            let results = ResizeArray()
            let searchPaths = 
              [ yield! targetFrameworkDirectories 
                yield! explicitIncludeDirs 
                yield fsharpCoreDir
                yield implicitIncludeDir 
                if System.Environment.OSVersion.Platform = System.PlatformID.Win32NT then 
                    yield! registrySearchPaths() ]

            for (r, baggage) in references do
                printfn "resolving %s" r
                let mutable found = false
                let success path = 
                    if not found then 
                        printfn "resolved %s --> %s" r path
                        found <- true
                        results.Add { itemSpec = path; prepareToolTip = snd; baggage=baggage } 

                try 
                    if not found && Path.IsPathRooted(r) then 
                        if FileSystem.SafeExists(r) then 
                            success r
                with e -> logWarning "SR001" (e.ToString())

                // For this one we need to get the version search exactly right, without doing a load
                try 
                    if not found && r.StartsWith("FSharp.Core, Version=")  && Environment.OSVersion.Platform = PlatformID.Win32NT then 
                        let n = AssemblyName(r)
                        let fscoreDir0 = 
                            let PF = 
                                match Environment.GetEnvironmentVariable("ProgramFiles(x86)") with
                                | null -> Environment.GetEnvironmentVariable("ProgramFiles")  
                                | s -> s 
                            PF + @"\Reference Assemblies\Microsoft\FSharp\.NETFramework\v4.0\"  + n.Version.ToString()
                        let trialPath = Path.Combine(fscoreDir0,n.Name + ".dll")
                        if FileSystem.SafeExists(trialPath) then 
                            success trialPath
                with e -> logWarning "SR001" (e.ToString())

                // Try to use Assemby.Load rather than searching paths for assemblies with explicit versions
                try 
                    if not found && r.Contains(",") then 
                        let ass = try Some (Assembly.Load(r)) with _ -> None
                        match ass with 
                        | None -> ()
                        | Some ass -> success ass.Location 
                with e -> logWarning "SR001" (e.ToString())

                let isFileName = 
                    r.EndsWith("dll",StringComparison.InvariantCultureIgnoreCase) ||
                    r.EndsWith("exe",StringComparison.InvariantCultureIgnoreCase)  

                let qual = if isFileName then r else try AssemblyName(r).Name + ".dll"  with _ -> r + ".dll"

                for searchPath in searchPaths do 
                  try 
                    if not found then 
                        let trialPath = Path.Combine(searchPath,qual)
                        if FileSystem.SafeExists(trialPath) then 
                            success trialPath
                  with e -> logWarning "SR001" (e.ToString())

                try 
                    // Seach the GAC on Windows
                    if not found && not isFileName && Environment.OSVersion.Platform = PlatformID.Win32NT then 
                        let n = AssemblyName(r)
                        let netfx = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
                        let gac = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(netfx.TrimEnd('\\'))),"assembly")
                        for gacdir in Directory.EnumerateDirectories(gac) do 
                            let assdir = Path.Combine(gacdir,n.Name)
                            if Directory.Exists(assdir) then 
                                let verdir = Path.Combine(assdir,"v4.0_"+n.Version.ToString()+"__"+String.concat "" [| for b in n.GetPublicKeyToken() -> sprintf "%02x" b |])
                                printfn "searching GAC: %s" verdir

                                if Directory.Exists(verdir) then 
                                    let trialPath = Path.Combine(verdir,qual)
                                    printfn "searching GAC: %s" trialPath
                                    if FileSystem.SafeExists(trialPath) then 
                                        success trialPath
                with e -> logWarning "SR001" (e.ToString())


                //if not found then 
                //    let ass = try Some (Assembly.Load(r)) with _ -> None
                //    match ass with 
                //    | Some ass -> success ass.Location 
                //    | None -> ()
            results.ToArray() }

#if INTERACTIVE
SimulatedMSBuildResolver.DotNetFrameworkReferenceAssembliesRootDirectory
SimulatedMSBuildResolver.HighestInstalledNetFrameworkVersion()

let fscoreDir = 
    if System.Environment.OSVersion.Platform = System.PlatformID.Win32NT then // file references only valid on Windows 
        let PF = 
            match Environment.GetEnvironmentVariable("ProgramFiles(x86)") with
            | null -> Environment.GetEnvironmentVariable("ProgramFiles")  // if PFx86 is null, then we are 32-bit and just get PF
            | s -> s 
        PF + @"\Reference Assemblies\Microsoft\FSharp\.NETFramework\v4.0\4.4.0.0"  
    else 
        System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()

let resolve s = 
    SimulatedMSBuildResolver.Resolve(ResolutionEnvironment.CompileTimeLike,[| for a in s -> (a, "") |],"v4.5.1", [SimulatedMSBuildResolver.DotNetFrameworkReferenceAssembliesRootDirectory + @"\v4.5.1" ],"", "", fscoreDir,[],__SOURCE_DIRECTORY__,ignore, (fun _ _ -> ()), (fun _ _-> ()))

resolve ["System"; "mscorlib"; "mscorlib.dll"; "FSharp.Core"; "FSharp.Core.dll"; "Microsoft.SqlServer.Dmf.dll"; "Microsoft.SqlServer.Dmf"  ]

resolve [ "FSharp.Core, Version=4.4.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a" ]

resolve [                 "EventViewer, Version=6.3.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35" ]
#endif

let GetDefaultResolver(msbuildEnabled: bool, msbuildVersion: string option) = 
    let msbuildEnabled = msbuildEnabled && false
    let msbuildVersion = defaultArg msbuildVersion  "12"
    let tryMSBuild v = 
        // Detect if MSBuild is on the machine, if so use the resolver from there
        let mb = try Assembly.Load(sprintf "Microsoft.Build.Framework, Version=%s.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a" v) |> Option.ofObj with _ -> None
        let ass = mb |> Option.bind (fun _ -> try Assembly.Load(sprintf "FSharp.Compiler.Service.MSBuild.v%s" v) |> Option.ofObj with _ -> None)
        let ty = ass |> Option.bind (fun ass -> ass.GetType("Microsoft.FSharp.Compiler.MSBuildReferenceResolver") |> Option.ofObj)
        let obj = ty |> Option.bind (fun ty -> ty.InvokeMember("get_Resolver",BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.InvokeMethod ||| BindingFlags.NonPublic, null, null, [| |]) |> Option.ofObj)
        let resolver = obj |> Option.bind (fun obj -> match obj with :? Resolver as r -> Some r | _ -> None)
        resolver
    match (if msbuildEnabled then tryMSBuild msbuildVersion else None) with 
    | Some r -> r
    | None -> 
    //match tryMSBuild "15" with 
    //| Some r -> r
    //| None -> 
    //match tryMSBuild "14" with 
    //| Some r -> r
    //| None -> 
    match (if msbuildEnabled && msbuildVersion <> "12" then tryMSBuild "12" else None) with 
    | Some r -> r
    | None -> 
    SimulatedMSBuildResolver 
