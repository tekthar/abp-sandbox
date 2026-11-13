#!/bin/sh
# AbpSandbox's courier (docs/adr/0002): the only part of a task that talks to object storage. It reads the manifest
# of signed links once, keeps it in /private (its own), and runs the jobs it lists in turn: each job's input is a tar
# the provider wrote; its result is a tar of what the work wrote, uploaded as is. It never opens what the work wrote,
# it only tars it (links stay links).
set -u
if [ -n "${ABPSANDBOX_MANIFEST:-}" ]; then
  umask 077
  curl -sf --retry 5 --retry-all-errors -o /private/manifest "$ABPSANDBOX_MANIFEST" || exit 3
  # Again without the link in the environment, so nothing can read it from this process.
  exec env -i PATH=/usr/bin:/bin /bin/sh /abp/courier.sh
fi

field() { awk -v k="$1" -v n="$2" '$1 == k && $2 == n { print $3 }' /private/manifest; }
idle="$(awk '$1 == "idle" { print $2 }' /private/manifest)"
max="$(awk '$1 == "max" { print $2 }' /private/manifest)"
while [ ! -e /sandbox/.abp/init ]; do sleep 0.1; done

n=1
while :; do
  in="$(field in "$n")"
  out="$(field out "$n")"
  [ -z "$in" ] || [ -z "$out" ] && break
  # Wait for job N, polling; past the idle time the task ends.
  tries=0
  until curl -sf -o /private/job.tar "$in"; do
    tries=$((tries + 1))
    [ $((tries / 4)) -ge "$idle" ] && break 2
    sleep 0.25
  done
  rm -rf "/sandbox/.abp/jobs/$n.part" && mkdir "/sandbox/.abp/jobs/$n.part"
  tar -x -f /private/job.tar -C "/sandbox/.abp/jobs/$n.part" || break
  chmod -R a+rX "/sandbox/.abp/jobs/$n.part"
  kind="$(cat "/sandbox/.abp/jobs/$n.part/kind")"
  mkdir -m 0777 "/sandbox/.abp/out/$n"
  mv "/sandbox/.abp/jobs/$n.part" "/sandbox/.abp/jobs/$n"
  : > "/sandbox/.abp/jobs/$n/ready"
  if [ "$kind" = stop ]; then
    : > /sandbox/.abp/stop
    printf '0\n' > /private/exit && tar -c -f /private/result.tar -C /private exit
    curl -sf -T /private/result.tar "$out" > /dev/null
    break
  fi
  while [ ! -e "/sandbox/.abp/out/$n/exit" ]; do sleep 0.1; done
  tar -c -C "/sandbox/.abp/out/$n" . 2> /dev/null | head -c "$max" > /private/result.tar
  curl -sf --retry 3 -T /private/result.tar "$out" > /dev/null || break
  rm -rf "/sandbox/.abp/jobs/$n" "/sandbox/.abp/out/$n" /private/job.tar /private/result.tar 2> /dev/null
  n=$((n + 1))
done
: > /sandbox/.abp/stop
