{
  description = "Sockseek CLI, daemon and NixOS service";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable-small";

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
        sockseek = nixpkgs.legacyPackages.${system}.callPackage ./nix/package.nix { };
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
