{ testers, package, lib }:
testers.runNixOSTest {
  name = "sockseek";
  nodes.machine = {
    imports = [ ./module.nix ];
    virtualisation.memorySize = 1536;
    services.sockseek = {
      enable = true;
      inherit package;
      settings = {
        no-config-watch = true; # Exercise explicit reloads even when filesystem events are disabled.
        username = "fixture";
        user-description = ''
          First line

          # My music
        '';
        password-file = "/run/sockseek-test/password";
        web-password-file = "/run/sockseek-test/web-password";
        allow-http-auth = true;
        mock-files-dir = "/run/sockseek-test/files";
        mock-files-no-read-tags = true;
        allowed-users = [ "Alice,Bob" ];
      };
      profiles = [
        {
          name = "songs";
          condition = ''download-mode == "song"'';
          settings.allowed-users.append = [ "local" ];
        }
        {
          name = "fixture";
          settings.output-dir = "/var/lib/sockseek/selected";
        }
      ];
    };
    specialisation.live.configuration.services.sockseek = {
      settings.user-description = lib.mkForce "Updated without disconnecting";
      profiles = [ { name = "after-reload"; } ];
    };
    specialisation.new-port.configuration.services.sockseek.settings.server-port = 5031;

    systemd.services.sockseek = {
      serviceConfig.RuntimeDirectory = "sockseek-test";
      preStart = ''
        umask 077
        cd /run/sockseek-test
        echo peer-test > password
        echo browser-test > web-password
        mkdir -p files
        echo music > 'files/Fixture Artist - Fixture Song.mp3'
      '';
    };
  };
  testScript = ''
    import json
    from shlex import quote

    machine.start()
    machine.wait_for_unit("sockseek.service")
    machine.wait_for_open_port(5030)
    url = "http://127.0.0.1:5030"
    assert "data-docs" in machine.succeed(f"curl -fsS {url}/")
    assert "<title>Install on NixOS" in machine.succeed(f"curl -fsS {url}/docs/installation/nixos.html")

    def request(path, body=None):
        command = "curl -fsS -b /tmp/cookies -c /tmp/cookies"
        if body is not None:
            token = json.loads(request("/api/auth/session"))["antiforgeryToken"]
            command += " -H " + quote("X-CSRF-Token: " + token) + " --json " + quote(json.dumps(body))
        return machine.succeed(command + " " + quote(url + path))

    def login():
        machine.succeed("rm -f /tmp/cookies")
        request("/api/auth/login", {"username": "admin", "password": "browser-test"})

    login()
    result = json.loads(request("/api/jobs/downloads/song", {
        "songQuery": {"artist": "Fixture Artist", "title": "Fixture Song"},
        "options": {"profileNames": ["fixture"]}
    }))
    job = "/api/jobs/" + result["jobId"]
    machine.wait_until_succeeds("find /var/lib/sockseek/selected -name '*.mp3' -exec cmp '/run/sockseek-test/files/Fixture Artist - Fixture Song.mp3' {} \\; -print | grep -q mp3", timeout=45)
    machine.wait_until_succeeds(f"curl -fsS -b /tmp/cookies '{url}/api/jobs?terminalOutcome=Succeeded' | grep -qi '{result['jobId']}'", timeout=30)
    transfer = json.loads(request("/api/transfers?direction=download"))["items"][0]["transferId"]
    pid_command = "systemctl show -p MainPID --value sockseek.service"
    pid = machine.succeed(pid_command).strip()
    switch = "/run/booted-system/specialisation"
    machine.succeed(f"{switch}/live/bin/switch-to-configuration test")
    machine.wait_until_succeeds(f"curl -fsS -b /tmp/cookies {url}/api/profiles | grep -q after-reload")
    assert machine.succeed(pid_command).strip() == pid
    assert not json.loads(request("/api/server/status"))["configuration"]["restartRequired"]

    machine.succeed(f"{switch}/new-port/bin/switch-to-configuration test")
    machine.wait_for_open_port(5031)
    assert machine.succeed(pid_command).strip() != pid
    url = "http://127.0.0.1:5031"
    login()
    assert json.loads(request(job))["summary"]["jobId"] == result["jobId"]
    assert json.loads(request("/api/transfers?direction=download"))["items"][0]["transferId"] == transfer
    machine.succeed("test -s /var/lib/sockseek/sockseek.db")
  '';
}
