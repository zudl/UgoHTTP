ARG BASE_IMAGE
FROM ${BASE_IMAGE}
COPY packages /packages
RUN --network=none apt-get --yes --no-install-recommends install /packages/*.deb && \
    dpkg-query -W > /opt-packages.txt && rm -rf /packages
# Debian's arm64 ntdll.so is marked BTI, yet its hand-written syscall dispatchers have no BTI landing pad: on a
# CPU with BTI (Apple M4) every syscall from PE code raises SIGILL and wineboot spins. Clearing the BTI bit makes
# ld.so map it unguarded; PAC stays.
RUN --network=none ntdll=/usr/lib/aarch64-linux-gnu/wine/aarch64-unix/ntdll.so; \
    [ ! -e "$ntdll" ] || perl -0777 -i -pe \
    's/(\x04\0\0\0\x10\0\0\0\x05\0\0\0GNU\0\0\0\0\xc0\x04\0\0\0)\x03\0\0\0/$1\x02\0\0\0/g == 1 or die "no BTI note\n"' \
    "$ntdll"
ENV WINEDEBUG=-all WINEPREFIX=/opt/wineprefix XDG_RUNTIME_DIR=/run/wine
RUN --network=none mkdir -m 700 /run/wine && /usr/lib/wine/wine64 wineboot --init && /usr/lib/wine/wineserver --wait
ENTRYPOINT ["/usr/lib/wine/wine64"]
