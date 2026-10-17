#!/usr/bin/env bash
# CI's coverage job, run on Linux before a push (docs/testing/COVERAGE.md). The ratchet holds every file to its
# baseline using numbers measured on Linux, and Windows takes different branches, so a Windows run cannot stand in for
# it. On the Windows workstation, run it inside WSL:
#
#   wsl -e bash /mnt/c/Projects/Deluno/scripts/coverage-ratchet-local.sh
#
# It copies the checkout to the Linux disk (much faster than building on /mnt/c), runs the backend tests with coverlet,
# the web unit tests with v8 and the Node tests with node --test coverage, exactly as ci.yml's backend, web and
# coverage-node jobs do, then merges them and runs the ratchet as the coverage job does. Needs .NET 10 at ~/.dotnet
# and Node 24 at ~/node (or both on PATH) and rsync.
set -uo pipefail

src=${DELUNO_SOURCE:-/mnt/c/Projects/Deluno}
dst=${DELUNO_LINUX_COPY:-$HOME/deluno-linux}
export PATH="$HOME/node/bin:$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

mkdir -p "$dst"
rsync -a --delete --exclude bin/ --exclude obj/ --exclude node_modules/ --exclude TestResults/ --exclude artifacts/ \
  --exclude .claude/ --exclude coverage-in/ --exclude coverage-out/ --exclude apps/web/coverage/ "$src/" "$dst/"
cd "$dst"
# An earlier run's results (rsync leaves excluded folders alone) would otherwise be merged as this run's.
rm -rf coverage-in coverage-out tests/*/TestResults apps/web/coverage
mkdir -p coverage-in/coverage-dotnet coverage-in/coverage-web coverage-in/coverage-node

echo "== .NET build (Release)"
dotnet build Deluno.slnx -c Release -v q 2>&1 | tail -3

failed=0
for project in tests/*/*.Tests.csproj; do
  name=$(basename "$project" .csproj)
  # The tray is a Windows app; CI's Linux backend job does not run its tests either.
  [ "$name" = "Deluno.Tray.Tests" ] && continue
  echo "== $name"
  dotnet test "$project" -c Release --no-build --no-restore --filter "Category!=Timing" \
    --collect:"XPlat Code Coverage" --settings tests/coverage.runsettings 2>&1 | grep -E "Passed!|Failed!|\[FAIL\]" | head -20
  [ "${PIPESTATUS[0]}" = 0 ] || failed=1
  mkdir -p "coverage-in/coverage-dotnet/$name"
  find "tests/$name/TestResults" -name coverage.opencover.xml | while read -r file; do
    cp "$file" "coverage-in/coverage-dotnet/$name/$(basename "$(dirname "$file")").opencover.xml"
  done
done

echo "== web unit tests with coverage"
npm ci --no-audit --no-fund >/dev/null 2>&1 || { echo "npm ci failed"; exit 1; }
npm run test:unit:web:coverage 2>&1 | grep -E "Test Files|Tests |FAIL" | head -20
cp apps/web/coverage/lcov.info coverage-in/coverage-web/lcov.info

echo "== Node tests with coverage"
node --test --experimental-test-coverage \
  --test-coverage-include="scripts/**/*.mjs" --test-coverage-exclude="**/*.test.mjs" \
  --test-reporter=dot --test-reporter-destination=stdout \
  --test-reporter=lcov --test-reporter-destination=coverage-in/coverage-node/scripts.lcov \
  "scripts/**/*.test.mjs" | tail -3
(cd services/metadata-gateway && node --test --experimental-test-coverage --test-coverage-include="src/**/*.js" \
  --test-reporter=dot --test-reporter-destination=stdout \
  --test-reporter=lcov --test-reporter-destination=../../coverage-in/coverage-node/metadata-gateway.lcov | tail -3)

echo "== merge and ratchet"
dotnet tool restore >/dev/null
dotnet tool run reportgenerator "-reports:coverage-in/coverage-dotnet/**/*.opencover.xml" \
  "-targetdir:coverage-out/dotnet" "-reporttypes:Cobertura" "-assemblyfilters:+Deluno.*;-Deluno.*.Tests" >/dev/null
node scripts/coverage/merge-coverage.mjs \
  --cobertura coverage-out/dotnet/Cobertura.xml \
  --lcov coverage-in/coverage-web/lcov.info=apps/web \
  --lcov coverage-in/coverage-node/scripts.lcov \
  --lcov coverage-in/coverage-node/metadata-gateway.lcov \
  --exclusions docs/testing/coverage-exclusions.json \
  --out-json coverage-out/coverage-report.json \
  --out-md coverage-out/coverage-report.md >/dev/null
node scripts/check-coverage-ratchet.mjs --report coverage-out/coverage-report.json
ratchet=$?

[ "$failed" = 0 ] || echo "note: some backend tests failed above, which lowers their files' coverage."
echo "COVERAGE-RATCHET-DONE ratchet=$ratchet tests_failed=$failed report=$dst/coverage-out/coverage-report.md"
exit $ratchet
