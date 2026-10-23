// Caddy's own main package (cmd/caddy/main.go of github.com/caddyserver/caddy/v2): the command and the standard
// module set, nothing else. Issue #188, ADR-0021 item 1. This file carries no compiler directive, and a Caddy bump
// that changes the upstream file changes this one in the same PR.
package main

import (
	caddycmd "github.com/caddyserver/caddy/v2/cmd"

	// The standard module set, as in the upstream release binary.
	_ "github.com/caddyserver/caddy/v2/modules/standard"
)

func main() {
	caddycmd.Main()
}
