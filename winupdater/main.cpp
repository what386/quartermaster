#include "updater.h"

#include <cstdio>
#include <limits>
#include <stdexcept>
#include <string>

namespace {
template <typename Char> std::uint32_t positive_number(const Char *text) {
  std::uint64_t value = 0;
  if (*text == 0)
    throw std::runtime_error("Expected a positive integer.");

  for (; *text != 0; ++text) {
    if (*text < '0' || *text > '9')
      throw std::runtime_error("Expected a positive integer.");

    value = value * 10 + static_cast<unsigned>(*text - '0');

    if (value > std::numeric_limits<std::uint32_t>::max())
      throw std::runtime_error("Integer is too large.");
  }
  if (value == 0)
    throw std::runtime_error("Expected a positive integer.");

  return static_cast<std::uint32_t>(value);
}

template <typename Char> int run(int argc, Char **argv) {
  if (argc != 5 && argc != 6 && argc != 7) {
    std::fprintf(
        stderr,
        "Usage: Quartermaster.Updater <pid> <staged-directory> "
        "<install-directory> <executable-name> [timeout-ms] [log-file]\n");
    return 2;
  }
  try {
    if (argc == 7) {
      FILE *log = nullptr;
      if (_wfreopen_s(&log, argv[6], L"ab", stderr) != 0)
        return 1;
    }
    const auto pid = positive_number(argv[1]);
    const auto timeout =
        std::chrono::milliseconds(argc >= 6 ? positive_number(argv[5]) : 60000);

    if (timeout > std::chrono::minutes(10))
      throw std::runtime_error("The exit timeout must be at most ten minutes.");

    const auto plan = updater::prepare_update(argv[2], argv[3], argv[4]);

    updater::wait_for_exit(pid, timeout);
    updater::apply_update(plan);
    std::fprintf(stderr, "Quartermaster update completed.\n");

    return 0;
  } catch (const std::exception &error) {
    std::fprintf(stderr, "Quartermaster update failed: %s\n", error.what());
    return 1;
  }
}
} // namespace

int wmain(int argc, wchar_t **argv) { return run(argc, argv); }
