{
  description = "Sockseek CLI, daemon and NixOS service";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable-small";

  # Released versions are prebuilt for x86-64 and ARM64. Nix offers this cache when
  # the flake is used directly (nix run, nix profile); NixOS hosts add it in nix.settings.
  nixConfig = {
    extra-substituters = [ "https://sockseek.cachix.org" ];
    extra-trusted-public-keys = [ "sockseek.cachix.org-1:LnckvcFZn2PnIgScUOlA4oGcmrgIJVpwzy0FXMUB/gk=" ];
  };

  outputs =
    { self, nixpkgs }:
    let
      forLinux = nixpkgs.lib.genAttrs [
        "x86_64-linux"
        "aarch64-linux"
      ];
    in
    {
      packages = forLinux (system: rec {
        sockseek = nixpkgs.legacyPackages.${system}.callPackage ./nix/package.nix {
          sourceRevision = self.rev or (self.dirtyRev or null);
        };
        default = sockseek;
      });

      nixosModules.default = { pkgs, lib, ... }: {
        imports = [ ./nix/module.nix ];
        services.sockseek.package = lib.mkDefault self.packages.${pkgs.stdenv.hostPlatform.system}.sockseek;
      };

      checks = forLinux (system: {
        service = nixpkgs.legacyPackages.${system}.callPackage ./nix/test.nix {
          package = self.packages.${system}.sockseek;
        };
      });
    };
}
