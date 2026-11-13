#!/bin/sh
# AbpSandbox's init (docs/adr/0002): runs once, as root, before the work and the courier.
# /sandbox is the volume both share; /private is the courier's alone.
set -eu
uid="${ABPSANDBOX_UID:-65534}"
gid="${ABPSANDBOX_GID:-65534}"

mkdir -p /sandbox/.abp/bin /sandbox/.abp/jobs /sandbox/.abp/out /sandbox/work /sandbox/tmp /private
cp /bin/busybox.static /sandbox/.abp/bin/busybox
cp /abp/work.sh /sandbox/.abp/work.sh

# The runner's own files: root's, not writable by the work.
chown root:root /sandbox /sandbox/.abp /sandbox/.abp/bin /sandbox/.abp/bin/busybox /sandbox/.abp/work.sh
chmod 0755 /sandbox /sandbox/.abp /sandbox/.abp/bin
chmod 0555 /sandbox/.abp/bin/busybox /sandbox/.abp/work.sh

# Jobs and results: the courier makes each job's folder; the work writes only inside a result's folder.
chown courier:courier /sandbox/.abp/jobs /sandbox/.abp/out
chmod 0755 /sandbox/.abp/jobs /sandbox/.abp/out
chown courier:courier /private
chmod 0700 /private

# The work's own folders.
chown "$uid:$gid" /sandbox/work
chmod 0755 /sandbox/work
chmod 1777 /sandbox/tmp
echo ready > /sandbox/.abp/init
chmod 0444 /sandbox/.abp/init
