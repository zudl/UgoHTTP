cmake_minimum_required(VERSION 3.25)
project(ugo_dependencies NONE)
include(ExternalProject)
include("${UGO_PINS}")
set(client_config "${CMAKE_CURRENT_LIST_DIR}/client_config.cmake")
# nghttp2 keeps assert() whatever NDEBUG says, so __FILE__ would carry the absolute checkout path into the binary.
get_filename_component(checkout "${CMAKE_CURRENT_LIST_DIR}/../.." ABSOLUTE)
set(common
    -DCMAKE_BUILD_TYPE=MinSizeRel
    -DCMAKE_INSTALL_PREFIX=${UGO_OUTPUT}
    -DCMAKE_INSTALL_LIBDIR=lib
    -DCMAKE_TOOLCHAIN_FILE=${UGO_TOOLCHAIN}
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON
    -DCMAKE_C_VISIBILITY_PRESET=hidden
    -DCMAKE_CXX_VISIBILITY_PRESET=hidden
    "-DCMAKE_C_FLAGS=-ffunction-sections -fdata-sections -fno-common -g0 -ffile-prefix-map=${checkout}=. ${UGO_SANITIZER_FLAGS}"
    -DCMAKE_PROJECT_INCLUDE=${client_config}
    -DCMAKE_EXE_LINKER_FLAGS=${UGO_SANITIZER_FLAGS}
    -DBUILD_SHARED_LIBS=OFF
    -DBUILD_TESTING=OFF)
function(dependency name)
    string(TOUPPER "${name}" pin)
    file(GLOB patches "${CMAKE_CURRENT_LIST_DIR}/patches/${name}-*.patch")
    list(SORT patches)
    set(patch_command)
    foreach(patch IN LISTS patches)
        find_program(UGO_PATCH patch REQUIRED)
        list(APPEND patch_command COMMAND "${UGO_PATCH}" -p1 --forward --batch --fuzz=0 -i "${patch}")
    endforeach()
    if(patch_command)
        list(POP_FRONT patch_command)
        list(PREPEND patch_command PATCH_COMMAND)
    endif()
    ExternalProject_Add(${name}
        URL "${${pin}_ARCHIVE}"
        URL_HASH SHA256=${${pin}_SHA256}
        DOWNLOAD_EXTRACT_TIMESTAMP TRUE
        ${patch_command}
        CMAKE_GENERATOR Ninja
        CMAKE_ARGS ${common} ${ARGN}
        BUILD_COMMAND ${CMAKE_COMMAND} --build <BINARY_DIR>
        INSTALL_COMMAND ${CMAKE_COMMAND} --install <BINARY_DIR>
        LOG_DOWNLOAD ON LOG_CONFIGURE ON LOG_BUILD ON LOG_INSTALL ON
        LOG_OUTPUT_ON_FAILURE ON)
endfunction()
dependency(mbedtls
    -DENABLE_PROGRAMS=OFF -DENABLE_TESTING=OFF -DGEN_FILES=OFF
    -DMBEDTLS_FATAL_WARNINGS=OFF -DLINK_WITH_PTHREAD=ON
    -DUSE_STATIC_MBEDTLS_LIBRARY=ON -DUSE_SHARED_MBEDTLS_LIBRARY=OFF)
dependency(zlib
    -DZLIB_BUILD_SHARED=OFF -DZLIB_BUILD_STATIC=ON -DZLIB_BUILD_TESTING=OFF)
# zlib names its static archive libzs.a on Windows; every consumer links libz.a.
file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/zlib_archive.cmake"
    "if(EXISTS \"${UGO_OUTPUT}/lib/libzs.a\")\n"
    "    file(RENAME \"${UGO_OUTPUT}/lib/libzs.a\" \"${UGO_OUTPUT}/lib/libz.a\")\n"
    "endif()\n")
ExternalProject_Add_Step(zlib archive_name
    COMMAND ${CMAKE_COMMAND} -P "${CMAKE_CURRENT_BINARY_DIR}/zlib_archive.cmake"
    DEPENDEES install)
dependency(nghttp2
    -DENABLE_LIB_ONLY=ON -DBUILD_STATIC_LIBS=ON
    -DENABLE_DOC=OFF -DENABLE_FAILMALLOC=OFF)
set(curl_options
    -DBUILD_CURL_EXE=OFF -DBUILD_STATIC_LIBS=ON
    -DENABLE_UNIX_SOCKETS=OFF
    -DBUILD_LIBCURL_DOCS=OFF -DBUILD_MISC_DOCS=OFF -DENABLE_CURL_MANUAL=OFF
    -DCURL_USE_MBEDTLS=ON -DCURL_USE_OPENSSL=OFF -DCURL_USE_SCHANNEL=OFF -DCURL_WINDOWS_SSPI=OFF
    -DCURL_USE_PKGCONFIG=OFF -DCURL_USE_CMAKECONFIG=ON
    -DCMAKE_PREFIX_PATH=${UGO_OUTPUT}
    -DMBEDTLS_INCLUDE_DIR=${UGO_OUTPUT}/include
    -DMBEDTLS_LIBRARY=${UGO_OUTPUT}/lib/libmbedtls.a
    -DMBEDX509_LIBRARY=${UGO_OUTPUT}/lib/libmbedx509.a
    -DMBEDCRYPTO_LIBRARY=${UGO_OUTPUT}/lib/libtfpsacrypto.a
    -DNGHTTP2_INCLUDE_DIR=${UGO_OUTPUT}/include
    -DNGHTTP2_LIBRARY=${UGO_OUTPUT}/lib/libnghttp2.a
    -DUSE_NGHTTP2=ON -DUSE_NGTCP2=OFF -DUSE_QUICHE=OFF
    -DZLIB_INCLUDE_DIR=${UGO_OUTPUT}/include
    -DZLIB_LIBRARY=${UGO_OUTPUT}/lib/libz.a
    -DCURL_ZLIB=ON -DCURL_BROTLI=OFF -DCURL_ZSTD=OFF
    -DCURL_USE_LIBPSL=OFF -DUSE_LIBIDN2=OFF
    -DCURL_USE_LIBSSH2=OFF -DCURL_USE_LIBSSH=OFF
    -DCURL_USE_GSSAPI=OFF -DCURL_USE_GSASL=OFF
    -DCURL_ENABLE_NTLM=OFF -DENABLE_THREADED_RESOLVER=ON -DENABLE_ARES=OFF
    -DCURL_DISABLE_HTTP=OFF -DCURL_DISABLE_WEBSOCKETS=OFF
    -DCURL_CA_BUNDLE=none -DCURL_CA_PATH=none -DCURL_CA_FALLBACK=OFF)
foreach(feature DICT FILE FTP GOPHER IMAP LDAP LDAPS MQTT POP3 RTSP SMTP TELNET TFTP
        ALTSVC COOKIES DOH HSTS NETRC IPFS PROXY KERBEROS_AUTH NEGOTIATE_AUTH AWS
        DIGEST_AUTH BASIC_AUTH BEARER_AUTH FORM_API GETOPTIONS HEADERS_API
        PROGRESS_METER SHUFFLE_DNS BINDLOCAL PARSEDATE LIBCURL_OPTION)
    list(APPEND curl_options -DCURL_DISABLE_${feature}=ON)
endforeach()
dependency(curl ${curl_options})
ExternalProject_Add_StepDependencies(curl configure mbedtls nghttp2 zlib)
