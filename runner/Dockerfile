# AbpSandbox's runner (docs/adr/0002): the init and courier containers of a cloud task. Pin the base by digest, and
# publish this image to the cloud's registry by digest; the provider's settings name it.
FROM alpine:3.24@sha256:f59abe656b692d901e184be3445e084786602108599d2a51d229c0f5a48c71bf
RUN apk add --no-cache curl busybox-static \
 && addgroup -g 65520 courier \
 && adduser -D -H -u 65520 -G courier courier
COPY init.sh courier.sh work.sh /abp/
RUN chmod 0555 /abp/init.sh /abp/courier.sh /abp/work.sh
