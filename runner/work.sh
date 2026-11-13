# AbpSandbox's work loop (docs/adr/0002): run by the copied busybox shell, as the sandbox's user, in the operator's
# image. It runs job after job the courier lays out in /sandbox/.abp/jobs/N, and writes each one's result in
# /sandbox/.abp/out/N, which the courier made for it.
B=/sandbox/.abp/bin/busybox
export TMPDIR=/sandbox/tmp
n=1
while :; do
  job="/sandbox/.abp/jobs/$n"
  out="/sandbox/.abp/out/$n"
  while [ ! -e "$job/ready" ]; do
    [ -e /sandbox/.abp/stop ] && exit 0
    "$B" sleep 0.1
  done
  kind="$("$B" cat "$job/kind")"
  cd /sandbox/work || exit 1
  case "$kind" in
    exec)
      time="$("$B" cat "$job/time")"
      cap="$("$B" cat "$job/cap")"
      start="$("$B" date +%s)"
      "$B" timeout -s KILL "$time" "$B" sh "$job/cmd.sh" > "$out/stdout.all" 2> "$out/stderr.all" < /dev/null
      code=$?
      end="$("$B" date +%s)"
      if [ "$code" -eq 137 ] && [ $((end - start)) -ge $((time - 1)) ]; then : > "$out/timedout"; fi
      for s in stdout stderr; do
        "$B" head -c "$cap" "$out/$s.all" > "$out/$s"
        [ "$("$B" stat -c %s "$out/$s.all")" -gt "$cap" ] && : > "$out/truncated"
        "$B" rm -f "$out/$s.all"
      done
      ;;
    put)
      "$B" cp -R "$job/files/." /sandbox/work/
      code=$?
      ;;
    get)
      # Paths one to a line, inside the work folder (the provider checked them); links stay links.
      "$B" tar -c -f "$out/files.tar" -C /sandbox/work -T "$job/paths" 2> "$out/stderr"
      code=$?
      ;;
    stop)
      exit 0
      ;;
    *)
      code=2
      ;;
  esac
  echo "$code" > "$out/exit.part" && "$B" mv "$out/exit.part" "$out/exit"
  n=$((n + 1))
done
