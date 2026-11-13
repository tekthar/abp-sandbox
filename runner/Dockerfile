# AbpSandbox's runner (docs/adr/0002): the init and courier containers of a cloud task. Pin the base by digest, and
# publish this image to the cloud's registry by digest; the provider's settings name it.
FROM alpine:3.20@sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc
RUN apk add --no-cache curl busybox-static \
 && addgroup -g 65520 courier \
 && adduser -D -H -u 65520 -G courier courier
COPY init.sh courier.sh work.sh /abp/
RUN chmod 0555 /abp/init.sh /abp/courier.sh /abp/work.sh
