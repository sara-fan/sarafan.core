#!/usr/bin/env bash
# Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
# All rights reserved.
# This file is a part of the Sarafan application

set -euo pipefail

exec "$(dirname "$0")/bootstrap-cloud.sh" "$@"
