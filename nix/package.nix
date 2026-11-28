{
  lib,
  stdenv,
  buildDotnetModule,
  dotnetCorePackages,
  buildNpmPackage,
  nodejs_24,
  yt-dlp,
  sourceRevision ? null,
}:
let
  props = builtins.readFile ../Directory.Build.props;
  versionProperty = name: builtins.match ".*<${name}>([^<]+)</${name}>.*" props;
  prefix = lib.trim (builtins.head (versionProperty "VersionPrefix"));
  suffixMatch = versionProperty "VersionSuffix";
  suffix = if suffixMatch == null then "" else lib.trim (builtins.head suffixMatch);
  version = prefix + lib.optionalString (suffix != "") "-${suffix}";
  source =
    paths:
    lib.fileset.toSource {
      root = ../.;
      fileset = lib.fileset.intersection (lib.fileset.gitTracked ../.) (lib.fileset.unions paths);
    };
  assets =
    {
      directory,
      paths,
      hash,
      dist,
    }:
    buildNpmPackage {
      pname = "sockseek-${directory}";
      inherit version;
      src = source paths;
      sourceRoot = "source/${directory}";
      nodejs = nodejs_24;
      npmDepsHash = hash;
      # Disable interactive progress on the Nix build log terminal.
      env.CI = "true";
      installPhase = "cp -r ${dist} $out";
    };
  webui = assets {
    directory = "webui";
    paths = [
      ../webui
      ../docs/openapi.json
    ];
    hash = "sha256-aN3ANl3PhZ/VPC5I3bhGR8/Ef4fPElkbjjtbO1QEU1k=";
    dist = "dist";
  };
  manual = assets {
    directory = "docs";
    paths = [
      ../docs
      ../webui/public
    ];
    hash = "sha256-2wWo84+lF1z8PlB8SpT13clli5vb2bxqz2OwbnyafjI=";
    dist = ".vitepress/dist";
  };
in
buildDotnetModule {
  pname = "sockseek";
  inherit version;
  src = source [
    ../Sockseek.Api
    ../Sockseek.Cli
    ../Sockseek.Core
    ../Sockseek.HelpGenerator
    ../Sockseek.Persistence
    ../Sockseek.Server
    ../docs/pages
    ../Directory.Build.props
    ../Directory.Build.targets
    ../global.json
  ];
  projectFile = "Sockseek.Cli/Sockseek.Cli.csproj";
  # Refresh with: "$(nix build --no-link --print-out-paths .#sockseek.fetch-deps)" nix/deps.json
  nugetDeps = ./deps.json;
  dotnet-sdk = dotnetCorePackages.sdk_11_0-bin;
  dotnet-runtime = dotnetCorePackages.aspnetcore_11_0-bin;
  # The bundled libSkiaSharp.so (profile pictures) needs libstdc++ at runtime.
  runtimeDeps = [ stdenv.cc.cc.lib ];
  executables = [ "sockseek" ];
  makeWrapperArgs = [
    "--suffix"
    "PATH"
    ":"
    (lib.makeBinPath [ yt-dlp ])
  ];
  dotnetFlags = [
    # Nixpkgs verifies the archives and exposes an unpacked NuGet cache.
    "-p:RestoreForceEvaluate=true"
    # The Nix source has a fixed build path and no Git metadata.
    "-p:DeterministicSourcePaths=false"
    "-p:SkipWebUiBuild=true"
    "-p:OpenApiGenerateDocuments=false"
  ] ++ lib.optional (sourceRevision != null) "-p:SourceRevisionId=${sourceRevision}";
  # The CLI builds its help generator through an MSBuild target. Include that
  # project's dependencies when nixpkgs runs the restore-only fetch-deps helper.
  postConfigure = ''
    dotnet restore Sockseek.HelpGenerator/Sockseek.HelpGenerator.csproj --force-evaluate
  '';
  postInstall = ''
    ln -s ${webui} "$out/lib/sockseek/wwwroot"
    ln -s ${manual} "$out/lib/sockseek/manual"
  '';
  meta = {
    description = "Soulseek client, daemon and WebUI";
    homepage = "https://github.com/fiso64/sockseek";
    license = lib.licenses.agpl3Only;
    mainProgram = "sockseek";
    platforms = [
      "x86_64-linux"
      "aarch64-linux"
    ];
  };
}
