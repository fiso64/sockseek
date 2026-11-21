{
  config,
  lib,
  pkgs,
  ...
}:
let
  inherit (lib) mkOption types;
  cfg = config.services.sockseek;
  catalog = lib.importJSON ./settings.json;
  line = types.addCheck types.singleLineStr (s: !(lib.hasInfix "\r" s));
  listItem = types.addCheck line (s: builtins.match "[^[:space:]](.*[^[:space:]])?" s != null);
  itemList = types.listOf listItem;
  valueTypes = {
    string = line;
    inherit (types) int bool number;
    flag = types.bool;
    list = types.either itemList (
      types.submodule {
        options.append = mkOption {
          type = itemList;
          description = "Append these literal items to the inherited list.";
        };
      }
    );
  };

  settingsOption =
    forProfile:
    let
      allowed = lib.filterAttrs (_: entry: !(forProfile && (entry.defaultOnly or false))) catalog;
      fields = lib.mapAttrs (
        _: entry:
        mkOption {
          type = types.nullOr valueTypes.${entry.type};
          default = null;
          description = entry.description;
        }
      ) allowed;
    in
    mkOption {
      type = types.submodule { options = fields; };
      default = { };
      description = "Sockseek configuration settings. Omitted values use the application's defaults.";
    };

  render =
    key: value:
    let
      kind = catalog.${key}.type;
      scalar = if builtins.isString value then value else builtins.toJSON value;
      append = builtins.isAttrs value;
      items = if append then value.append else value;
    in
    if value == null || (kind == "flag" && !value) then
      [ ]
    else if kind == "list" then
      lib.optionals (!append || items != [ ]) (
        [ ("${key} =" + lib.optionalString append " +") ] ++ map (s: "  - ${s}") items
      )
    else
      [ ''${key} = "${scalar}"'' ];
  renderSettings = settings: lib.concatLists (lib.mapAttrsToList render settings);
  profileSection =
    p:
    [ "[${p.name}]" ]
    ++ lib.optional (p.condition != null) ''profile-cond = "${p.condition}"''
    ++ renderSettings p.settings;
  conf = pkgs.writeText "sockseek.conf" (
    lib.concatStringsSep "\n" (renderSettings cfg.settings ++ lib.concatMap profileSection cfg.profiles)
    + "\n"
  );
in
{
  options.services.sockseek = {
    enable = lib.mkEnableOption "the Sockseek daemon and WebUI";
    settings = settingsOption false;
    package = mkOption {
      type = types.package;
      default = pkgs.callPackage ./package.nix { };
      defaultText = lib.literalExpression "sockseek";
      description = "Sockseek package to run.";
    };
    profiles = mkOption {
      type = types.listOf (
        types.submodule {
          options = {
            settings = settingsOption true;
            name = mkOption {
              type = types.addCheck line (s: s != "" && s != "default");
              description = "Unique profile name.";
            };
            condition = mkOption {
              type = types.nullOr (types.addCheck line (s: s != ""));
              default = null;
              description = "Sockseek profile-cond expression for automatic selection.";
            };
          };
        }
      );
      default = [ ];
      description = "Named and automatic Sockseek profiles in application order.";
    };
  };

  config = lib.mkIf cfg.enable {
    assertions = [
      {
        assertion = lib.allUnique (map (p: p.name) cfg.profiles);
        message = "services.sockseek.profiles must have unique names.";
      }
    ];
    environment.systemPackages = [ cfg.package ];
    environment.etc."sockseek/sockseek.conf".source = conf;
    users.groups.sockseek = { };
    users.users.sockseek = {
      isSystemUser = true;
      group = "sockseek";
      home = "/var/lib/sockseek";
    };
    systemd.tmpfiles.rules = [ "d /var/lib/sockseek/downloads 0750 sockseek sockseek -" ];
    systemd.services.sockseek = {
      description = "Sockseek daemon and WebUI";
      after = [ "network.target" ];
      wantedBy = [ "multi-user.target" ];
      restartTriggers = [ conf ];
      environment.XDG_DATA_HOME = "/var/lib";
      serviceConfig = {
        User = "sockseek";
        Group = "sockseek";
        StateDirectory = "sockseek";
        StateDirectoryMode = "0750";
        WorkingDirectory = "/var/lib/sockseek/downloads";
        ExecStart = "${lib.getExe cfg.package} daemon --config /etc/sockseek/sockseek.conf";
        Restart = "on-failure";
        RestartSec = "5s";
        NoNewPrivileges = true;
      };
    };
  };
}
