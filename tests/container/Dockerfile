ARG BASE_IMAGE
FROM ${BASE_IMAGE}
COPY packages /packages
RUN --network=none apt-get --yes --no-install-recommends install /packages/*.deb && \
    dpkg-query -W > /opt-packages.txt && rm -rf /packages
COPY fault /usr/local/bin/fault
COPY selftest.py /opt/ugohttp/selftest.py
RUN chmod +x /usr/local/bin/fault
WORKDIR /work
CMD ["python3", "/opt/ugohttp/selftest.py"]
