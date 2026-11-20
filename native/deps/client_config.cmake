add_compile_definitions(
    CURL_STATICLIB
    MBEDTLS_USER_CONFIG_FILE="${CMAKE_CURRENT_LIST_DIR}/client_config.h"
    TF_PSA_CRYPTO_USER_CONFIG_FILE="${CMAKE_CURRENT_LIST_DIR}/client_config.h")
if(WIN32)
    include_directories("${CMAKE_CURRENT_LIST_DIR}")
    add_compile_definitions(NGHTTP2_STATICLIB)
endif()
# Set by a port's toolchain file, which every dependency project reads.
if(UGO_PLATFORM_CONFIG_FILE)
    add_compile_definitions(UGO_PLATFORM_CONFIG_FILE="${UGO_PLATFORM_CONFIG_FILE}")
endif()
