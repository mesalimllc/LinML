# Old-glibc build environment for the patched TensorRT-RTX plugin EP.
#
# Ubuntu 22.04 ships glibc 2.35 and libstdc++ 3.4.30, the floor of the stock
# NVIDIA wheel (manylinux_2_35). A library built here loads on 22.04, 24.04 and
# anything newer; one built on a current distro does not load on older ones.
# Used by scripts/build-trt-rtx-ep-docker.sh. The TensorRT-RTX SDK and the CUDA
# toolkit are NOT baked in (neither is redistributable): they are mounted at run
# time.
FROM ubuntu:22.04

ENV DEBIAN_FRONTEND=noninteractive

# build-essential: the compiler that defines the libstdc++ symbol floor.
# patchelf: forces RUNPATH=$ORIGIN. binutils: readelf, nm, objdump, strip.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        binutils build-essential ca-certificates curl git patchelf python3 \
    && rm -rf /var/lib/apt/lists/*

# The EP's dependencies (ONNX) need CMake 3.26; Ubuntu 22.04 ships 3.22. CMake
# is a build tool: it does not change which glibc or libstdc++ symbols the
# library ends up needing, so the official binary is fine here.
ARG CMAKE_VERSION=3.31.6
ARG CMAKE_SHA256=5a1133ff103c71eb5120e2cc3de922733e7d8a26a98ae716397e8676adb367bf
RUN curl -fsSL -o /tmp/cmake.tgz \
        "https://github.com/Kitware/CMake/releases/download/v${CMAKE_VERSION}/cmake-${CMAKE_VERSION}-linux-x86_64.tar.gz" \
    && echo "${CMAKE_SHA256}  /tmp/cmake.tgz" | sha256sum -c - \
    && tar -xzf /tmp/cmake.tgz -C /usr/local --strip-components=1 \
    && rm /tmp/cmake.tgz \
    && cmake --version
