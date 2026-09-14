#pragma once
#include "CoreDefines.h"

#ifdef _KERNEL_MODE
namespace HelixRuntime {
    class HString {
        char* data; size_t len;
    public:
        HString() : data(nullptr), len(0) {}
        HString(const char* str) {
            if (!str) { data = nullptr; len = 0; return; }
            len = 0; while (str[len]) len++;
            data = (char*)OSAlloc(len + 1);
            if (data) { for (size_t i = 0; i <= len; i++) data[i] = str[i]; }
        }
        HString(const HString& other) {
            len = other.len;
            if (other.data && len > 0) {
                data = (char*)OSAlloc(len + 1);
                if (data) { for (size_t i = 0; i <= len; i++) data[i] = other.data[i]; }
            }
            else { data = nullptr; }
        }
        HString& operator=(const HString& other) {
            if (this != &other) {
                OSFree(data); len = other.len;
                if (other.data && len > 0) {
                    data = (char*)OSAlloc(len + 1);
                    if (data) { for (size_t i = 0; i <= len; i++) data[i] = other.data[i]; }
                }
                else { data = nullptr; }
            }
            return *this;
        }
        ~HString() { OSFree(data); }
        bool operator==(const char* str) const {
            if (!data && !str) return true;
            if (!data || !str) return false;
            size_t i = 0; while (data[i] && str[i] && data[i] == str[i]) i++;
            return data[i] == str[i];
        }
        bool operator==(const HString& other) const { return *this == other.data; }
        const char* c_str() const { return data ? data : ""; }
    };

    template<typename T>
    class HVector {
        T* data; size_t capacity; size_t count;
    public:
        HVector() : data(nullptr), capacity(0), count(0) {}
        HVector(const HVector& other) : data(nullptr), capacity(0), count(0) {
            if (other.capacity > 0) {
                data = (T*)OSAlloc(other.capacity * sizeof(T));
                if (data) {
                    capacity = other.capacity;
                    for (size_t i = 0; i < other.count; i++) { new(&data[i]) T(other.data[i]); count++; }
                }
            }
        }
        HVector& operator=(const HVector& other) {
            if (this != &other) {
                clear(); OSFree(data); data = nullptr; capacity = 0; count = 0;
                if (other.capacity > 0) {
                    data = (T*)OSAlloc(other.capacity * sizeof(T));
                    if (data) {
                        capacity = other.capacity;
                        for (size_t i = 0; i < other.count; i++) { new(&data[i]) T(other.data[i]); count++; }
                    }
                }
            }
            return *this;
        }
        ~HVector() { clear(); OSFree(data); }

        void reserve(size_t new_cap) {
            if (new_cap <= capacity) return;
            T* new_data = (T*)OSAlloc(new_cap * sizeof(T));
            if (new_data) {
                for (size_t i = 0; i < count; i++) {
                    new(&new_data[i]) T(static_cast<T&&>(data[i])); data[i].~T();
                }
                OSFree(data); data = new_data; capacity = new_cap;
            }
        }

        void push_back(const T& item) {
            if (count >= capacity) {
                reserve(capacity == 0 ? 8 : capacity * 2);
                if (count >= capacity) return;
            }
            new(&data[count++]) T(item);
        }
        void clear() { for (size_t i = 0; i < count; i++) data[i].~T(); count = 0; }

        T* begin() { return data; }
        T* end() { return data + count; }
        const T* begin() const { return data; }
        const T* end() const { return data + count; }

        size_t size() const { return count; }
        const T& operator[](size_t idx) const { return data[idx]; }
        T& operator[](size_t idx) { return data[idx]; }

        T* erase(T* it) {
            if (it >= begin() && it < end()) {
                it->~T(); size_t idx = it - begin();
                for (size_t i = idx; i < count - 1; i++) {
                    new(&data[i]) T(static_cast<T&&>(data[i + 1])); data[i + 1].~T();
                }
                count--; return begin() + idx;
            }
            return it;
        }
    };

    template<typename K, typename V>
    class HMap {
    public:
        struct Pair { K first; V second; };
    private:
        HVector<Pair> elements;
    public:
        V& operator[](const K& key) {
            for (auto& pair : elements) { if (pair.first == key) return pair.second; }
            elements.push_back({ key, V() }); return elements[elements.size() - 1].second;
        }
        void erase(const K& key) {
            for (auto it = elements.begin(); it != elements.end(); ) {
                if (it->first == key) { it = elements.erase(it); return; }
                else { ++it; }
            }
        }
        void clear() { elements.clear(); }

        Pair* begin() { return elements.begin(); }
        Pair* end() { return elements.end(); }
        const Pair* begin() const { return elements.begin(); }
        const Pair* end() const { return elements.end(); }

        Pair* find(const K& key) {
            for (auto it = elements.begin(); it != elements.end(); ++it) {
                if (it->first == key) return it;
            }
            return end();
        }
    };
}
#else
#include <string>
#include <vector>
#include <unordered_map>
namespace HelixRuntime {
    using HString = std::string;
    template<typename T> using HVector = std::vector<T>;
    template<typename K, typename V> using HMap = std::unordered_map<K, V>;
}
#endif