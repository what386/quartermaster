#pragma once

#include <chrono>
#include <cstdint>
#include <filesystem>
#include <vector>

namespace updater {
namespace fs = std::filesystem;

struct UpdatePlan {
  fs::path source;
  fs::path target;
  fs::path executable;
  std::vector<fs::path> files;
};

fs::path process_executable();

void wait_for_exit(std::uint32_t pid, std::chrono::milliseconds timeout);
void restart(const fs::path &executable, const fs::path &working_directory);

UpdatePlan prepare_update(const fs::path &source, const fs::path &target,
                          const fs::path &executable);
void apply_update(const UpdatePlan &plan);
} // namespace updater
