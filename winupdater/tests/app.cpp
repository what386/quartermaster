#include <cstdio>

int main() {
    const auto marker = std::fopen("restarted.txt", "w");
    if (marker == nullptr) return 1;
    std::fputs("restarted", marker);
    return std::fclose(marker) == 0 ? 0 : 1;
}
